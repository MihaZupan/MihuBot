using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using MihuBot.Configuration;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// Molly transport: <c>recipient key ID (16) || ephemeral public key (32) ||
/// nonce (24) || ciphertext || tag (16)</c>. The pinned static public key is used
/// exclusively for discovery; application requests require a rotating, memory-only key.
/// </summary>
/// <remarks>
/// <para>
/// This authenticates the server, not the client: anyone knowing its public key can seal a request.
/// Independently generated rotating keys protect old application traffic after their deletion,
/// even if the static identity key is subsequently compromised. See README.md for the wire contract.
/// </para>
/// <para>
/// Because anyone holding the public key can seal, requests carry a timestamp and a random nonce.
/// The timestamp has to be within <see cref="TimestampTolerance"/> of the server's clock, and the
/// last <see cref="TrackedNonceCount"/> nonces are remembered and rejected, so a captured blob can't
/// simply be replayed. Response replay needs no separate guard: each request derives a unique session
/// key, so a response only ever decrypts for the exact request it answers.
/// </para>
/// </remarks>
public sealed class MollyRequestProtector : IDisposable
{
    /// <summary>The first 16 bytes of SHA-512 of the recipient's raw X25519 public key.</summary>
    public const int RecipientKeyIdLength = 16;

    public const int HeaderLength = RecipientKeyIdLength + EphemeralPublicKeyLength;

    /// <summary>The client's ephemeral public key in each request header.</summary>
    private const int EphemeralPublicKeyLength = X25519DiffieHellman.PublicKeySizeInBytes;

    /// <summary>Domain separation for the HKDF step, so this key derivation is bound to this protocol.</summary>
    private static ReadOnlySpan<byte> HkdfInfoLabel => "MihuBot.Molly.MollyRequestProtector.v2"u8;

    /// <summary>How far the client's clock may be off, in either direction.</summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(30);

    /// <summary>The request nonce, as raw bytes before base64 encoding.</summary>
    public const int RequestNonceLength = 16;

    /// <summary>How many recently seen nonces are remembered for replay detection.</summary>
    public const int TrackedNonceCount = 100_000;

    /// <summary>Length of <see cref="RequestNonceLength"/> bytes once base64 encoded.</summary>
    private const int EncodedNonceLength = (RequestNonceLength + 2) / 3 * 4;

    private readonly MollyTransportKeyRing _keys;

    private readonly TimeProvider _timeProvider;
    private readonly int _trackedNonceCount;

    /// <summary>Insertion ordered so the oldest nonce can be evicted once the window is full.</summary>
    private readonly Queue<string> _nonceOrder;
    private readonly HashSet<string> _nonces;
    private readonly Lock _nonceLock = new();

    public MollyRequestProtector(IConfiguration configuration)
        : this(Convert.FromBase64String(configuration[OptionalFeatures.MollyTransportPrivateKeyName]!))
    { }

    /// <summary>
    /// Exists so tests can supply key material, a clock, and a smaller replay window directly.
    /// </summary>
    /// <param name="privateKey">The server's raw 32-byte X25519 private key.</param>
    public MollyRequestProtector(ReadOnlySpan<byte> privateKey, TimeProvider? timeProvider = null, int trackedNonceCount = TrackedNonceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(privateKey.Length, X25519DiffieHellman.PrivateKeySizeInBytes, OptionalFeatures.MollyTransportPrivateKeyName);
        ArgumentOutOfRangeException.ThrowIfLessThan(trackedNonceCount, 1);

        _timeProvider = timeProvider ?? TimeProvider.System;
        _trackedNonceCount = trackedNonceCount;

        _nonceOrder = new Queue<string>(trackedNonceCount);
        _nonces = new HashSet<string>(trackedNonceCount, StringComparer.Ordinal);

        _keys = new MollyTransportKeyRing(privateKey, _timeProvider);
    }

    public MollyTransportKeyResponse GetTransportKey() => _keys.GetCurrentKey();

    /// <summary>
    /// Opens a sealed request and checks that it is fresh and hasn't been seen before. On success,
    /// <paramref name="responseKey"/> holds the 32-byte key for encrypting the response.
    /// A rejected request consumes nothing, so a client whose clock is off can retry after correcting it.
    /// </summary>
    /// <remarks>The nonce is only remembered once the request is accepted.</remarks>
    public bool TryDecryptRequest(ReadOnlySpan<byte> body, [NotNullWhen(true)] out MollyApiRequest? request, [NotNullWhen(true)] out byte[]? responseKey)
    {
        request = null;
        responseKey = null;

        if (body.Length < HeaderLength + XAesGcm.CombinedOverheadInBytes + sizeof(ushort))
        {
            return false;
        }

        if (!TryDeriveSessionKeys(body.Slice(0, HeaderLength), out byte[]? requestKey, out byte[]? derivedResponseKey, out bool bootstrap))
        {
            // A low-order / contributory ephemeral key (all-zero agreement) or other derivation
            // failure - the ephemeral key is attacker-controlled, so this is just a bad request.
            return false;
        }

        using (var aead = new XAesGcm(requestKey))
        {
            CryptographicOperations.ZeroMemory(requestKey);

            if (!aead.TryDecrypt(body.Slice(HeaderLength), out byte[]? plaintext))
            {
                // Not sealed to our key, tampered with, or a bad ephemeral key.
                return false;
            }

            if (!BinaryPrimitives.TryReadUInt16BigEndian(plaintext, out ushort paddingLength) ||
                paddingLength > plaintext.Length - sizeof(ushort))
            {
                return false;
            }

            try
            {
                request = JsonSerializer.Deserialize<MollyApiRequest>(plaintext.AsSpan(sizeof(ushort) + paddingLength));
            }
            catch (JsonException)
            {
                // Decrypted, but not a request at all.
                return false;
            }

            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (request is null ||
            string.IsNullOrEmpty(request.Action) ||
            bootstrap != string.Equals(request.Action, MollyApiActions.TransportKey, StringComparison.Ordinal) ||
            (bootstrap && request.Data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) ||
            !IsTimestampFresh(request.Timestamp) ||
            !TryDecodeNonce(request.Nonce, out string? nonce) ||
            !TryConsumeNonce(nonce))
        {
            request = null;
            return false;
        }

        responseKey = derivedResponseKey;
        return true;
    }

    /// <summary>
    /// Encrypts a response under the per-request <paramref name="responseKey"/> from
    /// <see cref="TryDecryptRequest"/>. That key is unique to the request, so the response only
    /// decrypts for the client that made it - no nonce echo is needed to bind them.
    /// </summary>
    public byte[] EncryptResponse(MollyApiResponse response, ReadOnlySpan<byte> responseKey)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(response);
        int paddingLength = data.Length <= 1024 ? 1024 - data.Length : RandomNumberGenerator.GetInt32(513);
        byte[] plaintext = new byte[checked(sizeof(ushort) + paddingLength + data.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(plaintext, (ushort)paddingLength);
        RandomNumberGenerator.Fill(plaintext.AsSpan(sizeof(ushort), paddingLength));
        data.CopyTo(plaintext, sizeof(ushort) + paddingLength);
        CryptographicOperations.ZeroMemory(data);

        using var aead = new XAesGcm(responseKey);
        byte[] encrypted = aead.Encrypt(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return encrypted;
    }

    /// <summary>
    /// ECDH followed by HKDF to separate 32-byte request and response keys, bound to the recipient
    /// key ID and the client's ephemeral public key.
    /// Direction separation prevents reflecting a request ciphertext as a response.
    /// </summary>
    /// <returns>
    /// False if the agreement is rejected - a low-order ephemeral key yields an all-zero shared secret,
    /// which the platform throws on per RFC 7748 6.1. The ephemeral key is attacker-controlled, so this
    /// has to be a graceful rejection rather than an unhandled exception.
    /// </returns>
    private bool TryDeriveSessionKeys(ReadOnlySpan<byte> header, [NotNullWhen(true)] out byte[]? requestKey, [NotNullWhen(true)] out byte[]? responseKey, out bool bootstrap)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(header.Length, HeaderLength);

        requestKey = null;
        responseKey = null;
        bootstrap = false;
        Span<byte> shared = stackalloc byte[X25519DiffieHellman.SecretAgreementSizeInBytes];
        Span<byte> keys = stackalloc byte[2 * XAesGcm.KeySizeInBytes];

        try
        {
            if (!_keys.TryDeriveSharedSecret(header.Slice(0, RecipientKeyIdLength), header.Slice(RecipientKeyIdLength),
                shared, out bootstrap))
            {
                return false;
            }

            Span<byte> info = stackalloc byte[HkdfInfoLabel.Length + HeaderLength];
            HkdfInfoLabel.CopyTo(info);
            header.CopyTo(info.Slice(HkdfInfoLabel.Length));

            HKDF.DeriveKey(HashAlgorithmName.SHA512, shared, keys, salt: default, info);
            requestKey = keys.Slice(0, XAesGcm.KeySizeInBytes).ToArray();
            responseKey = keys.Slice(XAesGcm.KeySizeInBytes).ToArray();
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(keys);
        }
    }

    public void Dispose() => _keys.Dispose();

    /// <summary>Unix seconds, which have to be within <see cref="TimestampTolerance"/> of the server's clock.</summary>
    private bool IsTimestampFresh(long timestamp)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Guard against values so far out that constructing the DateTimeOffset would throw.
        if (timestamp < DateTimeOffset.UnixEpoch.ToUnixTimeSeconds() ||
            timestamp > now.AddYears(100).ToUnixTimeSeconds())
        {
            return false;
        }

        TimeSpan difference = DateTimeOffset.FromUnixTimeSeconds(timestamp) - now;

        return difference.Duration() <= TimestampTolerance;
    }

    /// <summary>
    /// Requires the exact base64 encoding of <see cref="RequestNonceLength"/> random bytes, so that
    /// the same nonce can't be re-sent in a different encoding to slip past the replay window.
    /// </summary>
    private static bool TryDecodeNonce(string? value, [NotNullWhen(true)] out string? nonce)
    {
        nonce = null;

        Span<byte> bytes = stackalloc byte[RequestNonceLength];

        if (value is null ||
            value.Length != EncodedNonceLength ||
            !Convert.TryFromBase64Chars(value, bytes, out int written) ||
            written != RequestNonceLength)
        {
            return false;
        }

        // Canonical form, so that whitespace or alternative padding can't produce a second spelling.
        nonce = Convert.ToBase64String(bytes);
        return true;
    }

    /// <summary>Records the nonce, or fails if it is already in the window of the last ones seen.</summary>
    private bool TryConsumeNonce(string nonce)
    {
        lock (_nonceLock)
        {
            if (!_nonces.Add(nonce))
            {
                return false;
            }

            _nonceOrder.Enqueue(nonce);

            if (_nonceOrder.Count > _trackedNonceCount)
            {
                _nonces.Remove(_nonceOrder.Dequeue());
            }

            return true;
        }
    }
}
