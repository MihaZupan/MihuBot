using System.Buffers.Text;
using System.IO.Hashing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;

namespace MihuBot.Tests.Molly;

/// <summary>
/// Covers the encrypted transport in isolation: sealing to the server's X25519 key, the timestamp
/// freshness window, and the rolling nonce replay window.
/// </summary>
public sealed class MollyRequestProtectorTests : IDisposable
{
    private delegate bool DeriveSessionKeys(ReadOnlySpan<byte> header, out byte[]? requestKey, out byte[]? responseKey, out bool bootstrap);

    /// <summary>Small enough to fill within a test, so nonce eviction is reachable.</summary>
    private const int SmallWindow = 4;

    private readonly ManualTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly MollyRequestProtector _protector;
    private readonly MollyTestEnvelope _client;

    public MollyRequestProtectorTests()
    {
        _protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time);
        _client = new MollyTestEnvelope(_protector.GetTransportKey());
    }

    private byte[] Encrypt(string action, string? data = null, string? nonce = null, long? timestamp = null) =>
        _client.EncryptRequest(action, data, nonce, timestamp ?? _time.GetUtcNow().ToUnixTimeSeconds());

    [Theory]
    [InlineData(0)]
    [InlineData(47)]
    [InlineData(49)]
    [InlineData(64)]
    public void KeyDerivation_RejectsAnIncorrectHeaderLength(int length)
    {
        DeriveSessionKeys derive = typeof(MollyRequestProtector)
            .GetMethod("TryDeriveSessionKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<DeriveSessionKeys>(_protector);

        Assert.Throws<ArgumentOutOfRangeException>(() => derive(new byte[length], out _, out _, out _));
    }

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
        using var wrongKey = new MollyTestEnvelope(MollyTestKeys.OtherTransportPublicKeyBytes);
        byte[] body = wrongKey.EncryptRequest("login", timestamp: _time.GetUtcNow().ToUnixTimeSeconds());
        MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(_protector.GetTransportKey().PublicKey)).CopyTo(body, 0);

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
    [InlineData(48 + XAesGcm.NonceSizeInBytes + XAesGcm.TagSizeInBytes + 2 - 1)] // One byte short of the padding-length field.
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
    public void ANonceWithDifferentPaddingBits_CannotBypassTheReplayWindow()
    {
        const string canonical = "AAAAAAAAAAAAAAAAAAAAAA==";
        const string alternative = "AAAAAAAAAAAAAAAAAAAAAB==";

        Assert.True(_protector.TryDecryptRequest(Encrypt("ping", nonce: canonical), out _, out _));
        Assert.False(_protector.TryDecryptRequest(Encrypt("ping", nonce: alternative), out _, out _));
    }

    [Fact]
    public void CachedNonce_IsDerivedFromTheProcessSeed()
    {
        _time.Advance(TimeSpan.FromSeconds(7));
        string nonce = MollyTestEnvelope.NewNonce();
        Assert.True(_protector.TryDecryptRequest(Encrypt("ping", nonce: nonce), out _, out _));

        long seed = (long)typeof(MollyRequestProtector)
            .GetField("NonceCacheSeed", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        byte[] hash = XxHash128.Hash(Convert.FromBase64String(nonce), seed);
        string expected = Base64Url.EncodeToString(hash);

        string cached = Assert.Single(GetNonceCacheField<HashSet<string>>(_protector, "_nonces"));
        Assert.Equal(expected, cached);
        Assert.NotEqual(nonce, cached);
        Assert.Equal(22, cached.Length);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", cached);
        var (storedNonce, storedAt) = Assert.Single(GetNonceCacheField<Queue<(string Nonce, long StoredAt)>>(_protector, "_nonceOrder"));
        Assert.Same(cached, storedNonce);
        Assert.Equal(_time.GetTimestamp(), storedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(SmallWindow)]
    public void Eviction_DoesNotGrowTheCollectionsOrAcceptReplays(int window)
    {
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time, window);
        using var client = new MollyTestEnvelope(protector.GetTransportKey());
        var nonces = GetNonceCacheField<HashSet<string>>(protector, "_nonces");
        var order = GetNonceCacheField<Queue<(string Nonce, long StoredAt)>>(protector, "_nonceOrder");
        int setCapacity = nonces.EnsureCapacity(0);
        int queueCapacity = order.EnsureCapacity(0);
        var bodies = new Queue<byte[]>();

        for (int i = 0; i < window * 3; i++)
        {
            byte[] body = client.EncryptRequest("ping", timestamp: _time.GetUtcNow().ToUnixTimeSeconds());
            Assert.True(protector.TryDecryptRequest(body, out _, out _));
            bodies.Enqueue(body);
            if (bodies.Count > window)
            {
                bodies.Dequeue();
            }

            foreach (byte[] replay in bodies)
            {
                Assert.False(protector.TryDecryptRequest(replay, out _, out _));
            }

            Assert.Equal(bodies.Count, nonces.Count);
            Assert.Equal(bodies.Count, order.Count);
            Assert.Equal(setCapacity, nonces.EnsureCapacity(0));
            Assert.Equal(queueCapacity, order.EnsureCapacity(0));
        }
    }

    private static T GetNonceCacheField<T>(MollyRequestProtector protector, string name) =>
        (T)typeof(MollyRequestProtector).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(protector)!;

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(12, 12)]
    [InlineData(29, 29)]
    [InlineData(30, 30)]
    [InlineData(45, 30)]
    public void Eviction_AdaptsTimestampToleranceToResidenceTime(int ageSeconds, int expectedToleranceSeconds)
    {
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time, trackedNonceCount: 3);
        using var client = new MollyTestEnvelope(protector.GetTransportKey());
        for (int i = 0; i < 3; i++)
        {
            Assert.True(protector.TryDecryptRequest(
                client.EncryptRequest("ping", timestamp: _time.GetUtcNow().ToUnixTimeSeconds()), out _, out _));
        }

        _time.Advance(TimeSpan.FromSeconds(ageSeconds));
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        Assert.True(protector.TryDecryptRequest(client.EncryptRequest("ping", timestamp: now), out _, out _));
        Assert.Equal(TimeSpan.FromSeconds(expectedToleranceSeconds),
            GetNonceCacheField<TimeSpan>(protector, "_timestampTolerance"));

        Assert.False(protector.TryDecryptRequest(client.EncryptRequest("ping", timestamp: now - expectedToleranceSeconds - 1), out _, out _));
        Assert.False(protector.TryDecryptRequest(client.EncryptRequest("ping", timestamp: now + expectedToleranceSeconds + 1), out _, out _));
        Assert.True(protector.TryDecryptRequest(client.EncryptRequest("ping", timestamp: now - expectedToleranceSeconds), out _, out _));
        Assert.True(protector.TryDecryptRequest(client.EncryptRequest("ping", timestamp: now + expectedToleranceSeconds), out _, out _));
    }

    [Fact]
    public void TimestampTolerance_OnlyDecreasesAsEntriesArriveFaster()
    {
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time, trackedNonceCount: 1);
        using var client = new MollyTestEnvelope(protector.GetTransportKey());
        Assert.True(protector.TryDecryptRequest(
            client.EncryptRequest("ping", timestamp: _time.GetUtcNow().ToUnixTimeSeconds()), out _, out _));

        foreach (var (ageSeconds, expectedToleranceSeconds) in new (double, double)[] { (12.5, 12.5), (20, 12.5), (7, 7), (0, 5), (45, 5) })
        {
            _time.Advance(TimeSpan.FromSeconds(ageSeconds));
            Assert.True(protector.TryDecryptRequest(
                client.EncryptRequest("ping", timestamp: _time.GetUtcNow().ToUnixTimeSeconds()), out _, out _));
            Assert.Equal(TimeSpan.FromSeconds(expectedToleranceSeconds),
                GetNonceCacheField<TimeSpan>(protector, "_timestampTolerance"));
        }
    }

    [Theory]
    [InlineData(-120)]
    [InlineData(120)]
    public void EvictionAge_UsesMonotonicTimeRatherThanTheWallClock(int clockAdjustmentSeconds)
    {
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time, trackedNonceCount: 1);
        using var client = new MollyTestEnvelope(protector.GetTransportKey());
        Assert.True(protector.TryDecryptRequest(
            client.EncryptRequest("ping", timestamp: _time.GetUtcNow().ToUnixTimeSeconds()), out _, out _));

        _time.Advance(TimeSpan.FromSeconds(12));
        _time.AdjustUtcNow(TimeSpan.FromSeconds(clockAdjustmentSeconds));
        Assert.True(protector.TryDecryptRequest(
            client.EncryptRequest("ping", timestamp: _time.GetUtcNow().ToUnixTimeSeconds()), out _, out _));
        Assert.Equal(TimeSpan.FromSeconds(12), GetNonceCacheField<TimeSpan>(protector, "_timestampTolerance"));
    }

    [Fact]
    public void RejectedRequests_DoNotEvictEntriesOrTightenTolerance()
    {
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time, trackedNonceCount: 1);
        using var client = new MollyTestEnvelope(protector.GetTransportKey());
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        byte[] accepted = client.EncryptRequest("ping", timestamp: now);
        Assert.True(protector.TryDecryptRequest(accepted, out _, out _));
        var order = GetNonceCacheField<Queue<(string Nonce, long StoredAt)>>(protector, "_nonceOrder");
        var original = Assert.Single(order);

        _time.Advance(TimeSpan.FromSeconds(1));
        string nonce = MollyTestEnvelope.NewNonce();
        Assert.False(protector.TryDecryptRequest(accepted, out _, out _));
        Assert.False(protector.TryDecryptRequest(client.EncryptRequest("ping", nonce: nonce, timestamp: now - 30), out _, out _));
        Assert.Equal(original, Assert.Single(order));
        Assert.Equal(MollyRequestProtector.TimestampTolerance, GetNonceCacheField<TimeSpan>(protector, "_timestampTolerance"));

        Assert.True(protector.TryDecryptRequest(
            client.EncryptRequest("ping", nonce: nonce, timestamp: _time.GetUtcNow().ToUnixTimeSeconds()), out _, out _));
        Assert.Equal(MollyRequestProtector.MinimumTimestampTolerance, GetNonceCacheField<TimeSpan>(protector, "_timestampTolerance"));
    }

    [Fact]
    public void OnceTheWindowRollsOver_TheOldestNonceCanBeUsedAgain()
    {
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, _time, SmallWindow);
        using var client = new MollyTestEnvelope(protector.GetTransportKey());

        string first = MollyTestEnvelope.NewNonce();
        Assert.True(protector.TryDecryptRequest(client.EncryptRequest("ping", nonce: first), out _, out _));

        // Push the first nonce out of the window of the last SmallWindow seen.
        for (int i = 0; i < SmallWindow; i++)
        {
            Assert.True(protector.TryDecryptRequest(client.EncryptRequest("ping", nonce: MollyTestEnvelope.NewNonce()), out _, out _));
        }

        Assert.True(protector.TryDecryptRequest(client.EncryptRequest("ping", nonce: first), out _, out _));
    }

    [Fact]
    public void ALowOrderEphemeralKey_IsRejectedRatherThanThrowing()
    {
        // An all-zero X25519 public key is a low-order point: the ECDH yields an all-zero shared
        // secret, which the platform rejects (RFC 7748 6.1). Since the ephemeral key comes straight
        // off the wire, that must surface as a rejected request, not an escaping exception.
        byte[] body = Encrypt("ping");
        body.AsSpan(16, 32).Clear();

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

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            _timestamp += elapsed.Ticks;
        }

        public void AdjustUtcNow(TimeSpan adjustment) => now += adjustment;
    }

    public void Dispose()
    {
        _client.Dispose();
        _protector.Dispose();
    }
}
