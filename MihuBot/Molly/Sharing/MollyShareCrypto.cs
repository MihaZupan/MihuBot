using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using MihuBot.Helpers.Crypto;

#nullable enable

namespace MihuBot.Molly.Sharing;

/// <summary>
/// The symmetric primitives of Molly file sharing (Sharing/README.md): per-purpose AEAD subkeys,
/// request authentication, party hashes and the at-rest encryption of share rows. SHA-512 throughout.
/// </summary>
internal static class MollyShareCrypto
{
    public const string SkeyRosterInnerLabel = "skey/roster-inner";
    public const string SkeyShareLabel = "skey/share";
    public const string SharedDataKeyLabel = "shared/data-key";
    public const string DataFileLabel = "data/file";

    public const int KeyLength = 32;
    public const int MacLength = HMACSHA512.HashSizeInBytes;
    public const int PartyHashLength = HMACSHA512.HashSizeInBytes;
    public const int MinDatabaseKeyLength = 32;
    public const int ServerKeyLength = HMACSHA512.HashSizeInBytes;

    /// <summary>The ASCII prefix of every label, without a terminator.</summary>
    public static ReadOnlySpan<byte> Prefix => "MihuBot.Molly.Share.v1/"u8;

    private static ReadOnlySpan<byte> RequestAuthLabel => "MihuBot.Molly.Share.Auth.v1"u8;

    private static ReadOnlySpan<byte> ServerKeyLabel => "server-key"u8;

    private static ReadOnlySpan<byte> PartyLabel => "party"u8;

    private static ReadOnlySpan<byte> AtRestLabel => "at-rest"u8;

    private static ReadOnlySpan<byte> RequestLabel => "request"u8;

    /// <summary><c>HKDF-SHA512(IKM = key, salt = empty, info = P || label || ENTRY, L = 32)</c>.</summary>
    public static void DeriveSubkey(ReadOnlySpan<byte> key, string label, ReadOnlySpan<byte> entry, Span<byte> subkey)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(key.Length, KeyLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(entry.Length, MollyShareCodec.EntryLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(subkey.Length, KeyLength);

        byte[] info = [.. Prefix, .. Encoding.ASCII.GetBytes(label), .. entry];
        HKDF.DeriveKey(HashAlgorithmName.SHA512, key, subkey, salt: default, info);
    }

    /// <summary><c>P || label || 0x00 || ENTRY</c>.</summary>
    public static byte[] GetAssociatedData(string label, ReadOnlySpan<byte> entry)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(entry.Length, MollyShareCodec.EntryLength);

        return [.. Prefix, .. Encoding.ASCII.GetBytes(label), 0, .. entry];
    }

    /// <summary><c>Enc(key, label, m)</c>: <c>nonce (24) || ciphertext || tag (16)</c> under the label's subkey.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, string label, ReadOnlySpan<byte> entry, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> subkey = stackalloc byte[KeyLength];

        try
        {
            DeriveSubkey(key, label, entry, subkey);

            using var aead = new XAesGcm(subkey);
            return aead.Encrypt(plaintext, GetAssociatedData(label, entry));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }
    }

    /// <summary>Reverses <see cref="Encrypt"/>. False for anything that doesn't authenticate.</summary>
    public static bool TryDecrypt(ReadOnlySpan<byte> key, string label, ReadOnlySpan<byte> entry, ReadOnlySpan<byte> ciphertext, [NotNullWhen(true)] out byte[]? plaintext)
    {
        Span<byte> subkey = stackalloc byte[KeyLength];

        try
        {
            DeriveSubkey(key, label, entry, subkey);

            using var aead = new XAesGcm(subkey);
            return aead.TryDecrypt(ciphertext, out plaintext, GetAssociatedData(label, entry));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }
    }

    /// <summary>
    /// <c>HMAC-SHA512(authKey, ACI || KEY)</c>, where <c>authKey</c> is HKDF-SHA512 of the
    /// identity/transport agreement, bound to the exchange and the action.
    /// </summary>
    public static byte[] ComputeRequestMac(ReadOnlySpan<byte> agreement, ReadOnlySpan<byte> recipientKeyId, ReadOnlySpan<byte> clientEphemeralPublicKey, ReadOnlySpan<byte> requestNonce, string action, ReadOnlySpan<byte> aci, ReadOnlySpan<byte> identityKey)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(agreement.Length, X25519DiffieHellman.SecretAgreementSizeInBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(recipientKeyId.Length, MollyRequestProtector.RecipientKeyIdLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(clientEphemeralPublicKey.Length, X25519DiffieHellman.PublicKeySizeInBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(requestNonce.Length, MollyRequestProtector.RequestNonceLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(aci.Length, MollyShareCodec.AciLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(identityKey.Length, MollyShareCodec.IdentityKeyLength);

        byte[] info = [.. RequestAuthLabel, .. recipientKeyId, .. clientEphemeralPublicKey, .. requestNonce, .. Encoding.UTF8.GetBytes(action)];
        Span<byte> authKey = stackalloc byte[MacLength];

        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA512, agreement, authKey, salt: default, info);

            byte[] message = [.. aci, .. identityKey];
            return HMACSHA512.HashData(authKey, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authKey);
        }
    }

    /// <summary>Recomputes the request MAC and compares it in constant time.</summary>
    public static bool VerifyRequestMac(ReadOnlySpan<byte> agreement, MollyExchangeContext exchange, string action, ReadOnlySpan<byte> aci, ReadOnlySpan<byte> identityKey, ReadOnlySpan<byte> mac)
    {
        if (mac.Length != MacLength)
        {
            return false;
        }

        byte[] expected = ComputeRequestMac(agreement, exchange.RecipientKeyId, exchange.ClientEphemeralPublicKey, exchange.RequestNonce, action, aci, identityKey);
        return CryptographicOperations.FixedTimeEquals(expected, mac);
    }

    /// <summary>
    /// <c>serverKey = HKDF-SHA512(IKM = databaseKey, salt = empty, info = P || "server-key", L = 64)</c>, the key of every
    /// party hash, request digest and at-rest key. Never <c>databaseKey</c> itself: the login path HMACs client-chosen
    /// input under it and stores the result, so a client could otherwise make S store one of these values in the clear.
    /// </summary>
    public static byte[] DeriveServerKey(ReadOnlySpan<byte> databaseKey)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(databaseKey.Length, MinDatabaseKeyLength);

        byte[] serverKey = new byte[ServerKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA512, databaseKey, serverKey, salt: default, [.. Prefix, .. ServerKeyLabel]);
        return serverKey;
    }

    /// <summary><c>HMAC-SHA512(serverKey, P || "party" || ACI || KEY)</c>, how identities are stored and matched.</summary>
    public static byte[] ComputePartyHash(ReadOnlySpan<byte> serverKey, ReadOnlySpan<byte> aci, ReadOnlySpan<byte> identityKey)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(serverKey.Length, ServerKeyLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(aci.Length, MollyShareCodec.AciLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(identityKey.Length, MollyShareCodec.IdentityKeyLength);

        return HMACSHA512.HashData(serverKey, [.. Prefix, .. PartyLabel, .. aci, .. identityKey]);
    }

    /// <summary>
    /// <c>HMAC-SHA512(serverKey, P || "request" || body)</c> over an entire create or update body, including SKEY and
    /// the signature, so only an identical retry matches. Keyed, so the database alone can't confirm guesses.
    /// </summary>
    public static byte[] ComputeRequestDigest(ReadOnlySpan<byte> serverKey, ReadOnlySpan<byte> body)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(serverKey.Length, ServerKeyLength);

        return HMACSHA512.HashData(serverKey, [.. Prefix, .. RequestLabel, .. body]);
    }

    /// <summary>
    /// Encrypts a share row field under <c>HMAC-SHA512(serverKey, P || "at-rest" || ENTRY)[..32]</c>,
    /// with <c>ENTRY || field</c> as associated data so ciphertexts can't move between entries or fields.
    /// <paramref name="field"/> is the field ID, followed by the slot (BE16) for per-trustee fields.
    /// </summary>
    public static byte[] EncryptAtRest(ReadOnlySpan<byte> serverKey, ReadOnlySpan<byte> entry, ReadOnlySpan<byte> field, ReadOnlySpan<byte> plaintext)
    {
        using XAesGcm aead = CreateAtRestAead(serverKey, entry);
        return aead.Encrypt(plaintext, [.. entry, .. field]);
    }

    /// <exception cref="CryptographicException">The stored field is malformed, tampered with or from another row.</exception>
    public static byte[] DecryptAtRest(ReadOnlySpan<byte> serverKey, ReadOnlySpan<byte> entry, ReadOnlySpan<byte> field, ReadOnlySpan<byte> ciphertext)
    {
        using XAesGcm aead = CreateAtRestAead(serverKey, entry);

        if (!aead.TryDecrypt(ciphertext, out byte[]? plaintext, [.. entry, .. field]))
        {
            throw new CryptographicException("A stored share field failed authentication.");
        }

        return plaintext;
    }

    private static XAesGcm CreateAtRestAead(ReadOnlySpan<byte> serverKey, ReadOnlySpan<byte> entry)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(serverKey.Length, ServerKeyLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(entry.Length, MollyShareCodec.EntryLength);

        Span<byte> hash = stackalloc byte[HMACSHA512.HashSizeInBytes];

        try
        {
            HMACSHA512.HashData(serverKey, [.. Prefix, .. AtRestLabel, .. entry], hash);
            return new XAesGcm(hash.Slice(0, XAesGcm.KeySizeInBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}
