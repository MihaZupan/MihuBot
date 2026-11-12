using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Molly.Attestation;

/// <summary>
/// The bytes the persistent signing key signs on every attested login. See Attestation.md for the
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
        var writer = new ArrayBufferWriter<byte>(128);

        WriteField(writer, ChallengeDomain);
        WriteField(writer, [Version]);
        WriteField(writer, exchange.RecipientKeyId);
        WriteField(writer, exchange.ClientEphemeralPublicKey);
        WriteField(writer, exchange.RequestNonce);

        return SHA512.HashData(writer.WrittenSpan);
    }

    public static byte[] Build(MollyAttestationMode mode, MollyExchangeContext exchange, ReadOnlySpan<byte> keyHash, ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> attestPublicKey, IReadOnlyList<byte[]> freshChain, IReadOnlyList<byte[]> signingKeyChain, IReadOnlyList<byte[]> attestKeyChain)
    {
        var writer = new ArrayBufferWriter<byte>(512);

        WriteField(writer, Domain);
        WriteField(writer, [Version]);
        WriteField(writer, Encoding.ASCII.GetBytes(mode.ToWireValue()));
        WriteField(writer, exchange.RecipientKeyId);
        WriteField(writer, exchange.ClientEphemeralPublicKey);
        WriteField(writer, keyHash);
        WriteField(writer, SHA512.HashData(signingPublicKey));
        WriteField(writer, attestPublicKey.IsEmpty ? [] : SHA512.HashData(attestPublicKey));
        WriteField(writer, HashChain(freshChain));
        WriteField(writer, HashChain(signingKeyChain));
        WriteField(writer, HashChain(attestKeyChain));

        return writer.WrittenSpan.ToArray();
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

    private static void WriteField(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(writer.GetSpan(sizeof(uint)), (uint)value.Length);
        writer.Advance(sizeof(uint));
        writer.Write(value);
    }
}
