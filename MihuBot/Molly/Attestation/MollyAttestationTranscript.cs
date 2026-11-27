using System.Buffers.Binary;
using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Molly.Attestation;

/// <summary>
/// The bytes the persistent signing key signs on every attested login. See Attestation/README.md for the
/// exact layout, which clients have to reproduce byte for byte.
/// </summary>
public static class MollyAttestationTranscript
{
    public const byte Version = 1;

    private static ReadOnlySpan<byte> Domain => "MihuBot.Molly.Attestation"u8;

    private static ReadOnlySpan<byte> ChallengeDomain => "MihuBot.Molly.AttestationChallenge"u8;

    /// <summary>
    /// The attestation challenge the per-login key must be generated with: SHA-512 over the exchange's
    /// recipient key ID, ephemeral key and request nonce. The transport key is unpredictable before the
    /// server publishes it, so the attestation can't predate it, and the ephemeral key and nonce make it
    /// unique to the request.
    /// </summary>
    public static byte[] Challenge(MollyExchangeContext exchange)
    {
        var writer = new ValueByteBuilder(stackalloc byte[256]);

        WriteField(ref writer, ChallengeDomain);
        WriteField(ref writer, [Version]);
        WriteField(ref writer, exchange.RecipientKeyId.AsSpan());
        WriteField(ref writer, exchange.ClientEphemeralPublicKey);
        WriteField(ref writer, exchange.RequestNonce);
        WriteField(ref writer, exchange.RequestTimestamp);

        byte[] challenge = SHA512.HashData(writer.AsSpan());
        writer.Dispose();
        return challenge;
    }

    public static byte[] Build(MollyAttestationMode mode, MollyExchangeContext exchange, ReadOnlySpan<byte> keyHash, ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> attestPublicKey, IReadOnlyList<byte[]> freshChain, IReadOnlyList<byte[]> signingKeyChain, IReadOnlyList<byte[]> attestKeyChain)
    {
        var writer = new ValueByteBuilder(stackalloc byte[1024]);

        WriteField(ref writer, Domain);
        WriteField(ref writer, [Version]);
        WriteField(ref writer, Encoding.ASCII.GetBytes(mode.ToWireValue()));
        WriteField(ref writer, exchange.RecipientKeyId.AsSpan());
        WriteField(ref writer, exchange.ClientEphemeralPublicKey);
        WriteField(ref writer, keyHash);
        WriteField(ref writer, SHA512.HashData(signingPublicKey));
        WriteField(ref writer, attestPublicKey.IsEmpty ? [] : SHA512.HashData(attestPublicKey));
        WriteField(ref writer, HashChain(freshChain));
        WriteField(ref writer, HashChain(signingKeyChain));
        WriteField(ref writer, HashChain(attestKeyChain));

        byte[] transcript = writer.ToArray();
        writer.Dispose();
        return transcript;
    }

    /// <summary><c>SHA-512(BE32(count) || (BE32(length) || der)...)</c>, over the exact submitted bytes.</summary>
    private static byte[] HashChain(IReadOnlyList<byte[]> chain)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)chain.Count);
        hash.AppendData(length);

        foreach (byte[] certificate in chain)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)certificate.Length);
            hash.AppendData(length);
            hash.AppendData(certificate);
        }

        return hash.GetHashAndReset();
    }

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
