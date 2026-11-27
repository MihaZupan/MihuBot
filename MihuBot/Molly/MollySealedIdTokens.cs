using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using MihuBot.Configuration;
using MihuBot.Helpers.Crypto;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// Seals an id and a timestamp into an opaque token, <c>base64(nonce || encrypted (id || ticks) || tag)</c>, that a
/// client can neither read nor forge or tamper with.
/// </summary>
/// <remarks>
/// XAES-256-GCM rather than plain AES-GCM because its 192-bit nonce can be randomly generated without a practical cap
/// on the number of tokens issued under one key. A 96-bit AES-GCM nonce would have limited the server to ~2^32 tokens
/// before nonce collisions - which are catastrophic for GCM - became a concern.
/// </remarks>
public sealed class MollySealedIdTokens
{
    private const int IdLength = 16; // Guid

    private const int PayloadLength = IdLength + sizeof(long);

    private const int TokenLength = XAesGcm.NonceSizeInBytes + PayloadLength + XAesGcm.TagSizeInBytes;

    /// <summary>Matches the minimum <see cref="MollyService"/> enforces for the database key.</summary>
    private const int MinDatabaseKeyLength = 32;

    private readonly XAesGcm _aead;
    private readonly bool _urlSafe;

    private int EncodedTokenLength => _urlSafe ? Base64Url.GetEncodedLength(TokenLength) : Base64.GetMaxEncodedToUtf8Length(TokenLength);

    /// <param name="urlSafe">Whether tokens are unpadded base64url rather than base64.</param>
    private MollySealedIdTokens(ReadOnlySpan<byte> key, bool urlSafe)
    {
        _aead = new XAesGcm(key);
        _urlSafe = urlSafe;
    }

    /// <summary>Under a key derived from the database key, so tokens stay valid across restarts.</summary>
    /// <param name="derivationInfo">Domain separation so this key can never coincide with another use of the database key.</param>
    public static MollySealedIdTokens CreateFromDatabaseKey(ReadOnlySpan<byte> databaseKey, ReadOnlySpan<byte> derivationInfo, bool urlSafe)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(databaseKey.Length, MinDatabaseKeyLength, OptionalFeatures.MollyDatabaseKeyName);

        Span<byte> key = stackalloc byte[XAesGcm.KeySizeInBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA512, databaseKey, key, salt: default, derivationInfo);

        try
        {
            return new MollySealedIdTokens(key, urlSafe);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Under a random key, so tokens are only valid within this instance.</summary>
    public static MollySealedIdTokens CreateEphemeral(bool urlSafe) => new(RandomNumberGenerator.GetBytes(XAesGcm.KeySizeInBytes), urlSafe);

    public string Protect(Guid id, DateTime timestamp)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        bool wrote = id.TryWriteBytes(payload);
        Debug.Assert(wrote);
        BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(IdLength), timestamp.Ticks);

        byte[] token = _aead.Encrypt(payload);

        return _urlSafe ? Base64Url.EncodeToString(token) : Base64.EncodeToString(token);
    }

    /// <summary>Fails for anything not issued under this key, including tampered tokens.</summary>
    public bool TryUnprotect([NotNullWhen(true)] string? token, out Guid id, out DateTime timestamp)
    {
        id = Guid.Empty;
        timestamp = DateTime.MinValue;

        if (token is null || token.Length != EncodedTokenLength)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[TokenLength];
        OperationStatus status = _urlSafe
            ? Base64Url.DecodeFromChars(token, bytes, out int consumed, out int written)
            : Base64.DecodeFromChars(token, bytes, out consumed, out written);

        if (status != OperationStatus.Done || consumed != token.Length || written != TokenLength)
        {
            return false;
        }

        if (!_aead.TryDecrypt(bytes, out byte[]? payload))
        {
            return false;
        }

        long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(IdLength));

        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        id = new Guid(payload.AsSpan(0, IdLength));
        timestamp = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}
