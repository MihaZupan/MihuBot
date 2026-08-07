using System.Buffers.Text;
using System.Security.Cryptography;
using MihuBot.Configuration;
using MihuBot.Helpers.Crypto;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// Wraps entry ids in an opaque, authenticated token so the database identifier is never handed to a client.
/// </summary>
/// <remarks>
/// The key is derived from the static database key, so tokens stay valid across restarts. Clients that
/// are already running but not currently logged in can keep pinging instead of being forced through a new
/// login every time the service is redeployed. XAES-256-GCM is used so the token is both encrypted and
/// authenticated - a client can neither read an id out of a token nor forge or tamper with one.
/// <para>
/// XAES-256-GCM rather than plain AES-GCM because its 192-bit nonce can be randomly generated without a
/// practical cap on the number of tokens issued under one key. A 96-bit AES-GCM nonce would have limited
/// the server to ~2^32 tokens before nonce collisions - which are catastrophic for GCM - became a concern.
/// </para>
/// </remarks>
public sealed class MollyIdProtector
{
    private const int IdLength = 16; // Guid

    private const int TokenLength = XAesGcm.NonceSizeInBytes + IdLength + XAesGcm.TagSizeInBytes;

    /// <summary>Matches the minimum <see cref="MollyService"/> enforces for the database key.</summary>
    private const int MinDatabaseKeyLength = 32;

    /// <summary>Domain separation so this key can never coincide with another use of the database key.</summary>
    private static ReadOnlySpan<byte> DerivationInfo => "MihuBot.Molly.MollyIdProtector.v1"u8;

    /// <summary>Length of <see cref="TokenLength"/> bytes once base64 encoded.</summary>
    private static readonly int EncodedTokenLength = Base64.GetMaxEncodedToUtf8Length(TokenLength);

    private readonly XAesGcm _aead;

    public MollyIdProtector(IConfiguration configuration)
        : this(Convert.FromBase64String(configuration[OptionalFeatures.MollyDatabaseKeyName]!))
    { }

    /// <summary>Exists so tests can supply key material directly.</summary>
    public MollyIdProtector(ReadOnlySpan<byte> databaseKey)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(databaseKey.Length, MinDatabaseKeyLength, OptionalFeatures.MollyDatabaseKeyName);

        Span<byte> key = stackalloc byte[XAesGcm.KeySizeInBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA512, databaseKey, key, salt: default, DerivationInfo);

        try
        {
            _aead = new XAesGcm(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Returns <c>base64(nonce || encrypted id || tag)</c>.</summary>
    public string Protect(Guid id)
    {
        Span<byte> idBytes = stackalloc byte[IdLength];
        bool wrote = id.TryWriteBytes(idBytes);
        Debug.Assert(wrote);

        return Convert.ToBase64String(_aead.Encrypt(idBytes));
    }

    /// <summary>
    /// Recovers the id from a token. Fails for anything this server didn't issue, including
    /// tokens issued under a different server key and tampered ones.
    /// </summary>
    public bool TryUnprotect(string? token, out Guid id)
    {
        id = Guid.Empty;

        Span<byte> bytes = stackalloc byte[TokenLength];

        if (token is null ||
            token.Length != EncodedTokenLength ||
            !Convert.TryFromBase64Chars(token, bytes, out int written) ||
            written != TokenLength)
        {
            return false;
        }

        if (!_aead.TryDecrypt(bytes, out byte[]? idBytes))
        {
            // Forged, tampered with, or issued under a different server key.
            return false;
        }

        id = new Guid(idBytes);
        return true;
    }
}
