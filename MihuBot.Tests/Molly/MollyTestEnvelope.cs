using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;

namespace MihuBot.Tests.Molly;

/// <summary>
/// The client half of the Molly transport: seals request bodies to the server's X25519 public key
/// and reads the encrypted responses back. Mirrors <see cref="MollyRequestProtector"/>'s ECDH + HKDF
/// derivation exactly, so an accidental change to either side fails the tests.
/// </summary>
public sealed class MollyTestEnvelope : IDisposable
{
    private const int EphemeralPublicKeyLength = 32;
    private const int RecipientKeyIdLength = 16;
    private const int HeaderLength = RecipientKeyIdLength + EphemeralPublicKeyLength;

    private static ReadOnlySpan<byte> HkdfInfoLabel => "MihuBot.Molly.MollyRequestProtector.v2"u8;

    private readonly byte[] _serverPublicKey;

    /// <summary>The key derived for the most recent request, reused to open its response.</summary>
    private byte[] _responseKey = [];

    public MollyTestEnvelope(byte[]? serverPublicKey = null)
    {
        _serverPublicKey = serverPublicKey ?? MollyTestKeys.TransportPublicKeyBytes;
    }

    public MollyTestEnvelope(MollyTransportKeyResponse key)
        : this(Convert.FromBase64String(key.PublicKey))
    { }

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(MollyRequestProtector.RequestNonceLength));

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static byte[] GetRecipientKeyId(ReadOnlySpan<byte> publicKey) =>
        SHA512.HashData(publicKey).AsSpan(0, RecipientKeyIdLength).ToArray();

    public static void MaskRequestBody(Span<byte> body)
    {
        for (int i = 0; i < RecipientKeyIdLength; i++)
        {
            body[i] ^= body[body.Length - RecipientKeyIdLength + i];
        }
    }

    /// <summary>The plaintext request envelope, with <paramref name="data"/> spliced in as raw JSON.</summary>
    public static string RequestJson(string action, string? data = null, string? nonce = null, long? timestamp = null) =>
        $$"""
        {"action":{{JsonSerializer.Serialize(action)}},"timestamp":{{timestamp ?? Now()}},"nonce":{{JsonSerializer.Serialize(nonce ?? NewNonce())}},"data":{{data ?? "null"}}}
        """;

    public byte[] EncryptRequest(string action, string? data = null, string? nonce = null, long? timestamp = null, int paddingLength = 0) =>
        Encrypt(RequestJson(action, data, nonce, timestamp), paddingLength);

    public byte[] Encrypt(string data, int paddingLength = 0) => Encrypt(Encoding.UTF8.GetBytes(data), paddingLength);

    public byte[] Encrypt(ReadOnlySpan<byte> data, int paddingLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(paddingLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paddingLength, ushort.MaxValue);

        byte[] plaintext = new byte[checked(2 + paddingLength + data.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(plaintext, (ushort)paddingLength);
        RandomNumberGenerator.Fill(plaintext.AsSpan(2, paddingLength));
        data.CopyTo(plaintext.AsSpan(2 + paddingLength));
        return EncryptRawPlaintext(plaintext);
    }

    /// <summary>Seals plaintext verbatim, including deliberately malformed padding frames.</summary>
    public byte[] EncryptRawPlaintext(ReadOnlySpan<byte> plaintext)
    {
        using X25519DiffieHellman ephemeral = X25519DiffieHellman.GenerateKey();
        byte[] ephemeralPublicKey = ephemeral.ExportPublicKey();

        byte[] header = new byte[HeaderLength];
        GetRecipientKeyId(_serverPublicKey).CopyTo(header, 0);
        ephemeralPublicKey.CopyTo(header, RecipientKeyIdLength);
        (byte[] requestKey, byte[] responseKey) = DeriveSessionKeys(ephemeral, header);
        CryptographicOperations.ZeroMemory(_responseKey);
        _responseKey = responseKey;

        using var aead = new XAesGcm(requestKey);
        byte[] sealedMessage = aead.Encrypt(plaintext);

        byte[] body = new byte[header.Length + sealedMessage.Length];
        header.CopyTo(body, 0);
        sealedMessage.CopyTo(body, header.Length);
        MaskRequestBody(body);
        return body;
    }

    public JsonElement DecryptResponse(byte[] body)
    {
        using var aead = new XAesGcm(_responseKey);
        Assert.True(aead.TryDecrypt(body, out byte[]? plaintext));

        try
        {
            Assert.True(plaintext.Length >= 2);
            int paddingLength = BinaryPrimitives.ReadUInt16BigEndian(plaintext);
            Assert.InRange(paddingLength, 0, plaintext.Length - 2);
            return JsonSerializer.Deserialize<JsonElement>(plaintext.AsSpan(2 + paddingLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private (byte[] RequestKey, byte[] ResponseKey) DeriveSessionKeys(X25519DiffieHellman ephemeral, ReadOnlySpan<byte> header)
    {
        Span<byte> shared = stackalloc byte[X25519DiffieHellman.SecretAgreementSizeInBytes];
        ephemeral.DeriveRawSecretAgreement(_serverPublicKey, shared);

        try
        {
            Span<byte> info = stackalloc byte[HkdfInfoLabel.Length + HeaderLength];
            HkdfInfoLabel.CopyTo(info);
            header.CopyTo(info.Slice(HkdfInfoLabel.Length));

            Span<byte> keys = stackalloc byte[64];
            HKDF.DeriveKey(HashAlgorithmName.SHA512, shared, keys, salt: default, info);
            return (keys.Slice(0, 32).ToArray(), keys.Slice(32).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_responseKey);
}
