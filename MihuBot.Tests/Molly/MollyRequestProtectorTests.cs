using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;

namespace MihuBot.Tests.Molly;

/// <summary>A clock the tests can hold still, so the timestamp window is deterministic.</summary>
file sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Covers the encrypted transport in isolation: sealing to the server's X25519 key, the timestamp
/// freshness window, and the rolling nonce replay window.
/// </summary>
public sealed class MollyRequestProtectorTests
{
    /// <summary>Small enough to fill within a test, so nonce eviction is reachable.</summary>
    private const int SmallWindow = 4;

    private readonly TimeProvider _time = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly MollyRequestProtector _protector;
    private readonly MollyTestEnvelope _client = new();

    public MollyRequestProtectorTests()
    {
        _protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time);
    }

    private byte[] Encrypt(string action, string? data = null, string? nonce = null, long? timestamp = null) =>
        _client.EncryptRequest(action, data, nonce, timestamp ?? _time.GetUtcNow().ToUnixTimeSeconds());

    [Fact]
    public void ValidRequest_IsDecrypted()
    {
        byte[] body = Encrypt("login", """{"keyHash":"AAAA"}""");

        Assert.True(_protector.TryDecryptRequest(body, out MollyApiRequest? request, out _));
        Assert.Equal("login", request.Action);
        Assert.Equal("AAAA", request.Data.GetProperty("keyHash").GetString());
    }

    [Fact]
    public void RequestSealedToADifferentServerKey_IsRejected()
    {
        var wrongKey = new MollyTestEnvelope(MollyTestKeys.OtherTransportPublicKeyBytes);
        byte[] body = wrongKey.EncryptRequest("login", timestamp: _time.GetUtcNow().ToUnixTimeSeconds());

        Assert.False(_protector.TryDecryptRequest(body, out _, out _));
    }

    [Fact]
    public void TamperedCiphertext_IsRejected()
    {
        byte[] body = Encrypt("login");
        body[^1] ^= 0xFF;

        Assert.False(_protector.TryDecryptRequest(body, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32 + XAesGcm.NonceSizeInBytes + XAesGcm.TagSizeInBytes - 1)] // One byte short of an empty payload.
    public void ARequestTooShortToBeAnEnvelope_IsRejected(int length)
    {
        Assert.False(_protector.TryDecryptRequest(new byte[length], out _, out _));
    }

    [Fact]
    public void PlaintextThatIsntTheExpectedShape_IsRejected()
    {
        Assert.False(_protector.TryDecryptRequest(_client.Encrypt("not json"), out _, out _));
        Assert.False(_protector.TryDecryptRequest(_client.Encrypt("123"), out _, out _));
        Assert.False(_protector.TryDecryptRequest(_client.Encrypt("null"), out _, out _));
    }

    [Fact]
    public void ARequestWithoutAnAction_IsRejected()
    {
        byte[] body = _client.Encrypt($$"""{"timestamp":{{_time.GetUtcNow().ToUnixTimeSeconds()}},"nonce":"{{MollyTestEnvelope.NewNonce()}}"}""");

        Assert.False(_protector.TryDecryptRequest(body, out _, out _));
    }

    [Fact]
    public void ATimestampAtTheEdgeOfTheWindow_IsAccepted()
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        long tolerance = (long)MollyRequestProtector.TimestampTolerance.TotalSeconds;

        Assert.True(_protector.TryDecryptRequest(Encrypt("ping", timestamp: now - tolerance), out _, out _));
        Assert.True(_protector.TryDecryptRequest(Encrypt("ping", timestamp: now + tolerance), out _, out _));
    }

    [Fact]
    public void ATimestampOutsideTheWindow_IsRejected()
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        long tolerance = (long)MollyRequestProtector.TimestampTolerance.TotalSeconds;

        Assert.False(_protector.TryDecryptRequest(Encrypt("ping", timestamp: now - tolerance - 1), out _, out _));
        Assert.False(_protector.TryDecryptRequest(Encrypt("ping", timestamp: now + tolerance + 1), out _, out _));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-1)]
    public void AnAbsurdTimestamp_IsRejectedRatherThanThrowing(long timestamp)
    {
        Assert.False(_protector.TryDecryptRequest(Encrypt("ping", timestamp: timestamp), out _, out _));
    }

    [Fact]
    public void AReusedNonce_IsRejected()
    {
        string nonce = MollyTestEnvelope.NewNonce();

        Assert.True(_protector.TryDecryptRequest(Encrypt("ping", nonce: nonce), out _, out _));
        Assert.False(_protector.TryDecryptRequest(Encrypt("ping", nonce: nonce), out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]                            // Decodes to 3 bytes, not 16.
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAA")]        // 24 chars but decodes to 18 bytes, not 16.
    public void AMalformedNonce_IsRejected(string? nonce)
    {
        byte[] body = _client.Encrypt($$"""{"action":"ping","timestamp":{{_time.GetUtcNow().ToUnixTimeSeconds()}},"nonce":{{JsonSerializer.Serialize(nonce)}}}""");

        Assert.False(_protector.TryDecryptRequest(body, out _, out _));
    }

    [Fact]
    public void ANonceInADifferentEncoding_CannotBypassTheReplayWindow()
    {
        byte[] raw = RandomNumberGenerator.GetBytes(MollyRequestProtector.RequestNonceLength);
        string canonical = Convert.ToBase64String(raw);

        Assert.True(_protector.TryDecryptRequest(Encrypt("ping", nonce: canonical), out _, out _));

        // A spelling with injected whitespace is a different string, but must not be treated as
        // a fresh nonce - it either decodes to the same bytes or is rejected outright.
        Assert.False(_protector.TryDecryptRequest(Encrypt("ping", nonce: canonical.Insert(4, " ")), out _, out _));
    }

    [Fact]
    public void OnceTheWindowRollsOver_TheOldestNonceCanBeUsedAgain()
    {
        var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time, SmallWindow);

        string first = MollyTestEnvelope.NewNonce();
        Assert.True(protector.TryDecryptRequest(Encrypt("ping", nonce: first), out _, out _));

        // Push the first nonce out of the window of the last SmallWindow seen.
        for (int i = 0; i < SmallWindow; i++)
        {
            Assert.True(protector.TryDecryptRequest(Encrypt("ping", nonce: MollyTestEnvelope.NewNonce()), out _, out _));
        }

        Assert.True(protector.TryDecryptRequest(Encrypt("ping", nonce: first), out _, out _));
    }

    [Fact]
    public void ALowOrderEphemeralKey_IsRejectedRatherThanThrowing()
    {
        // An all-zero X25519 public key is a low-order point: the ECDH yields an all-zero shared
        // secret, which the platform rejects (RFC 7748 6.1). Since the ephemeral key comes straight
        // off the wire, that must surface as a rejected request, not an escaping exception.
        byte[] body = new byte[32 + XAesGcm.NonceSizeInBytes + XAesGcm.TagSizeInBytes];

        Assert.False(_protector.TryDecryptRequest(body, out _, out _));
    }

    [Fact]
    public void Response_RoundTripsThroughTheClient()
    {
        // Opening a request establishes the per-request key the reply is encrypted under.
        Assert.True(_protector.TryDecryptRequest(Encrypt("ping"), out _, out byte[]? sessionKey));

        var response = new MollyApiResponse { Status = "ok" };
        byte[] body = _protector.EncryptResponse(response, sessionKey);

        JsonElement decrypted = _client.DecryptResponse(body);
        Assert.Equal("ok", decrypted.GetProperty("status").GetString());
    }

    [Fact]
    public void Configuration_RejectsAPrivateKeyOfTheWrongLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MollyRequestProtector(new byte[31]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MollyRequestProtector(new byte[33]));
    }
}
