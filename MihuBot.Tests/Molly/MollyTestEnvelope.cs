using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

/// <summary>
/// The client half of the Molly transport: seals request bodies to the server's X25519 public key
/// and reads the encrypted responses back. Mirrors <see cref="MollyRequestProtector"/>'s ECDH + HKDF
/// derivation exactly, so an accidental change to either side fails the tests.
/// </summary>
public sealed class MollyTestEnvelope
{
    private const int EphemeralPublicKeyLength = 32;

    private static ReadOnlySpan<byte> HkdfInfoLabel => "MihuBot.Molly.MollyRequestProtector.v1"u8;

    private readonly byte[] _serverPublicKey;

    /// <summary>The key derived for the most recent request, reused to open its response.</summary>
    private byte[] _sessionKey = [];

    public MollyTestEnvelope(byte[]? serverPublicKey = null)
    {
        _serverPublicKey = serverPublicKey ?? MollyTestKeys.TransportPublicKeyBytes;
    }

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(MollyRequestProtector.RequestNonceLength));

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The plaintext request envelope, with <paramref name="data"/> spliced in as raw JSON.</summary>
    public static string RequestJson(string action, string? data = null, string? nonce = null, long? timestamp = null) =>
        $$"""
        {"action":{{JsonSerializer.Serialize(action)}},"timestamp":{{timestamp ?? Now()}},"nonce":{{JsonSerializer.Serialize(nonce ?? NewNonce())}},"data":{{data ?? "null"}}}
        """;

    public byte[] EncryptRequest(string action, string? data = null, string? nonce = null, long? timestamp = null) =>
        Encrypt(RequestJson(action, data, nonce, timestamp));

    public byte[] Encrypt(string plaintext) => Encrypt(Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Seals to <c>ephemeral public key (32) || nonce (24) || ciphertext || tag (16)</c>.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        using X25519DiffieHellman ephemeral = X25519DiffieHellman.GenerateKey();
        byte[] ephemeralPublicKey = ephemeral.ExportPublicKey();

        byte[] key = DeriveSessionKey(ephemeral, ephemeralPublicKey);
        _sessionKey = key;

        using var aead = new XAesGcm(key);
        byte[] sealedMessage = aead.Encrypt(plaintext);

        byte[] body = new byte[ephemeralPublicKey.Length + sealedMessage.Length];
        ephemeralPublicKey.CopyTo(body, 0);
        sealedMessage.CopyTo(body, ephemeralPublicKey.Length);
        return body;
    }

    public JsonElement DecryptResponse(byte[] body)
    {
        using var aead = new XAesGcm(_sessionKey);
        Assert.True(aead.TryDecrypt(body, out byte[]? plaintext));

        return JsonSerializer.Deserialize<JsonElement>(plaintext);
    }

    private byte[] DeriveSessionKey(X25519DiffieHellman ephemeral, ReadOnlySpan<byte> ephemeralPublicKey)
    {
        Span<byte> shared = stackalloc byte[X25519DiffieHellman.SecretAgreementSizeInBytes];
        ephemeral.DeriveRawSecretAgreement(_serverPublicKey, shared);

        Span<byte> info = stackalloc byte[HkdfInfoLabel.Length + EphemeralPublicKeyLength + EphemeralPublicKeyLength];
        HkdfInfoLabel.CopyTo(info);
        ephemeralPublicKey.CopyTo(info.Slice(HkdfInfoLabel.Length));
        _serverPublicKey.CopyTo(info.Slice(HkdfInfoLabel.Length + EphemeralPublicKeyLength));

        byte[] key = new byte[XAesGcm.KeySizeInBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA512, shared, key, salt: default, info);
        return key;
    }
}
