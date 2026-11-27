using System.Security.Cryptography;
using MihuBot.Helpers.Crypto;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// Proof that a request comes from a device whose app is unlocked: a MAC under a key the device only holds in memory
/// while unlocked, bound to the request's action, exchange and session (see README.md, "Session proofs"). It travels in
/// the request envelope, so <see cref="MollyExchangeContext.SessionProof"/> carries it along with the exchange it's bound to.
/// </summary>
/// <remarks>
/// Session ids are kept in the clear on devices so that locked devices can keep checking in. The proof key is derived
/// from the entry's server secret, which only an attested login releases, and from the session id, so every login yields
/// a different key. The key itself never travels: the server derives it again from the stored secret.
/// </remarks>
public static class MollySessionProof
{
    private const byte Version = 1;

    public const int KeyLength = 64;

    private static ReadOnlySpan<byte> KeyInfo => "MihuBot.Molly.SessionProofKey"u8;

    private static ReadOnlySpan<byte> Domain => "MihuBot.Molly.SessionProof"u8;

    /// <summary><c>HKDF-SHA512(IKM = serverHmac, salt = UTF-8(sessionId), info = "MihuBot.Molly.SessionProofKey", L = 64)</c>.</summary>
    public static void DeriveKey(ReadOnlySpan<byte> serverHmac, string sessionId, Span<byte> key)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(key.Length, KeyLength);

        HKDF.DeriveKey(HashAlgorithmName.SHA512, serverHmac, key, Encoding.UTF8.GetBytes(sessionId), KeyInfo);
    }

    /// <summary>
    /// The proof a device holding <paramref name="serverHmac"/> sends within <paramref name="exchange"/>: standard padded base64 of
    /// an HMAC-SHA512 under the session's proof key, over a transcript whose fields are each <c>BE32(length) || bytes</c>.
    /// </summary>
    public static string Compute(ReadOnlySpan<byte> serverHmac, string sessionId, MollyExchangeContext exchange)
    {
        var transcript = new ValueByteBuilder(stackalloc byte[256]);

        WriteField(ref transcript, Domain);
        WriteField(ref transcript, [Version]);
        WriteField(ref transcript, Encoding.UTF8.GetBytes(exchange.Action));
        WriteField(ref transcript, exchange.RecipientKeyId.AsSpan());
        WriteField(ref transcript, exchange.ClientEphemeralPublicKey);
        WriteField(ref transcript, exchange.RequestNonce);
        WriteField(ref transcript, exchange.RequestTimestamp);
        WriteField(ref transcript, Encoding.UTF8.GetBytes(sessionId));

        Span<byte> key = stackalloc byte[KeyLength];
        DeriveKey(serverHmac, sessionId, key);

        try
        {
            return Convert.ToBase64String(HMACSHA512.HashData(key, transcript.AsSpan()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            transcript.Dispose();
        }
    }

    /// <summary>Whether the exchange's <see cref="MollyExchangeContext.SessionProof"/> is exactly the proof <see cref="Compute"/> gives.</summary>
    public static bool Verify(MollyExchangeContext exchange, ReadOnlySpan<byte> serverHmac, string sessionId) =>
        exchange.SessionProof is { } proof &&
        CryptographicOperations.FixedTimeEquals(Compute(serverHmac, sessionId, exchange), proof);

    private static void WriteField(ref ValueByteBuilder writer, scoped ReadOnlySpan<byte> value)
    {
        writer.AppendBigEndian((uint)value.Length);
        writer.Append(value);
    }

    /// <summary>A field holding <paramref name="value"/> as <c>BE64</c>.</summary>
    private static void WriteField(ref ValueByteBuilder writer, long value)
    {
        writer.AppendBigEndian((uint)sizeof(long));
        writer.AppendBigEndian(value);
    }
}
