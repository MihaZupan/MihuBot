using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using MihuBot.Configuration;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// The transport for the Molly API. Each request is sealed to the server's static X25519 public key
/// (hardcoded into the closed-source client) using ECDH + HKDF to derive a per-request XAES-256-GCM
/// key, so the wire is <c>ephemeral public key (32) || nonce (24) || ciphertext || tag (16)</c>. The
/// response is encrypted under that same derived key, as <c>nonce (24) || ciphertext || tag (16)</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is spam and metadata protection, not authentication - the public key is extractable from the
/// app, so anyone can seal a request. What it buys over a shared symmetric secret is that extracting
/// the app no longer lets an attacker decrypt anyone else's traffic: only the holder of the private
/// key (the server) can open a request, so the action, device token, or an alert's location stay
/// hidden from a passive eavesdropper who has reverse-engineered the client.
/// </para>
/// <para>
/// Because anyone holding the public key can seal, requests carry a timestamp and a random nonce.
/// The timestamp has to be within <see cref="TimestampTolerance"/> of the server's clock, and the
/// last <see cref="TrackedNonceCount"/> nonces are remembered and rejected, so a captured blob can't
/// simply be replayed. Response replay needs no separate guard: each request derives a unique session
/// key, so a response only ever decrypts for the exact request it answers.
/// </para>
/// </remarks>
public sealed class MollyRequestProtector
{
    /// <summary>The ephemeral public key that prefixes every request.</summary>
    private const int EphemeralPublicKeyLength = X25519DiffieHellman.PublicKeySizeInBytes;

    /// <summary>Domain separation for the HKDF step, so this key derivation is bound to this protocol.</summary>
    private static ReadOnlySpan<byte> HkdfInfoLabel => "MihuBot.Molly.MollyRequestProtector.v1"u8;

    /// <summary>How far the client's clock may be off, in either direction.</summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(30);

    /// <summary>The request nonce, as raw bytes before base64 encoding.</summary>
    public const int RequestNonceLength = 16;

    /// <summary>How many recently seen nonces are remembered for replay detection.</summary>
    public const int TrackedNonceCount = 100_000;

    /// <summary>Length of <see cref="RequestNonceLength"/> bytes once base64 encoded.</summary>
    private const int EncodedNonceLength = (RequestNonceLength + 2) / 3 * 4;

    /// <summary>The server's static key pair. Only its private half can open a sealed request.</summary>
    private readonly X25519DiffieHellman _privateKey;

    /// <summary>The matching public key, mixed into the HKDF context to bind the derived key to it.</summary>
    private readonly byte[] _publicKey;

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

        _privateKey = X25519DiffieHellman.ImportPrivateKey(privateKey);
        _publicKey = _privateKey.ExportPublicKey();
    }

    /// <summary>
    /// Opens a sealed request and checks that it is fresh and hasn't been seen before. On success,
    /// <paramref name="sessionKey"/> holds the per-request key the response must be encrypted under.
    /// A rejected request consumes nothing, so a client whose clock is off can retry after correcting it.
    /// </summary>
    /// <remarks>The nonce is only remembered once the request is accepted.</remarks>
    public bool TryDecryptRequest(ReadOnlySpan<byte> body, [NotNullWhen(true)] out MollyApiRequest? request, [NotNullWhen(true)] out byte[]? sessionKey)
    {
        request = null;
        sessionKey = null;

        if (body.Length < EphemeralPublicKeyLength + XAesGcm.CombinedOverheadInBytes)
        {
            return false;
        }

        if (!TryDeriveSessionKey(body.Slice(0, EphemeralPublicKeyLength), out byte[]? key))
        {
            // A low-order / contributory ephemeral key (all-zero agreement) or other derivation
            // failure - the ephemeral key is attacker-controlled, so this is just a bad request.
            return false;
        }

        using (var aead = new XAesGcm(key))
        {
            if (!aead.TryDecrypt(body.Slice(EphemeralPublicKeyLength), out byte[]? plaintext))
            {
                // Not sealed to our key, tampered with, or a bad ephemeral key.
                return false;
            }

            try
            {
                request = JsonSerializer.Deserialize<MollyApiRequest>(plaintext);
            }
            catch (JsonException)
            {
                // Decrypted, but not a request at all.
                return false;
            }
        }

        if (request is null ||
            string.IsNullOrEmpty(request.Action) ||
            !IsTimestampFresh(request.Timestamp) ||
            !TryDecodeNonce(request.Nonce, out string? nonce) ||
            !TryConsumeNonce(nonce))
        {
            request = null;
            return false;
        }

        sessionKey = key;
        return true;
    }

    /// <summary>
    /// Encrypts a response under the per-request <paramref name="sessionKey"/> from
    /// <see cref="TryDecryptRequest"/>. That key is unique to the request, so the response only
    /// decrypts for the client that made it - no nonce echo is needed to bind them.
    /// </summary>
    public byte[] EncryptResponse(MollyApiResponse response, ReadOnlySpan<byte> sessionKey)
    {
        using var aead = new XAesGcm(sessionKey);
        return aead.Encrypt(JsonSerializer.SerializeToUtf8Bytes(response));
    }

    /// <summary>
    /// ECDH against the request's ephemeral public key, run through HKDF to a 32-byte AEAD key. The
    /// ephemeral and server public keys are folded into the HKDF context so the key is bound to them.
    /// </summary>
    /// <returns>
    /// False if the agreement is rejected - a low-order ephemeral key yields an all-zero shared secret,
    /// which the platform throws on per RFC 7748 6.1. The ephemeral key is attacker-controlled, so this
    /// has to be a graceful rejection rather than an unhandled exception.
    /// </returns>
    private bool TryDeriveSessionKey(ReadOnlySpan<byte> ephemeralPublicKey, [NotNullWhen(true)] out byte[]? sessionKey)
    {
        Span<byte> shared = stackalloc byte[X25519DiffieHellman.SecretAgreementSizeInBytes];

        try
        {
            _privateKey.DeriveRawSecretAgreement(ephemeralPublicKey, shared);

            Span<byte> info = stackalloc byte[HkdfInfoLabel.Length + EphemeralPublicKeyLength + EphemeralPublicKeyLength];
            HkdfInfoLabel.CopyTo(info);
            ephemeralPublicKey.CopyTo(info.Slice(HkdfInfoLabel.Length));
            _publicKey.CopyTo(info.Slice(HkdfInfoLabel.Length + EphemeralPublicKeyLength));

            byte[] key = new byte[XAesGcm.KeySizeInBytes];
            HKDF.DeriveKey(HashAlgorithmName.SHA512, shared, key, salt: default, info);
            sessionKey = key;
            return true;
        }
        catch (CryptographicException)
        {
            sessionKey = null;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

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
