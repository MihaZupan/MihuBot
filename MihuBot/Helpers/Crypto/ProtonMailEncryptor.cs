using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;

#nullable enable

namespace MihuBot.Helpers.Crypto;

/// <summary>
/// Encrypts alert bodies for Proton Mail recipients using their published OpenPGP key, so the body
/// is opaque to the Azure email service it passes through. Recipients without a Proton key aren't
/// mailed at all. The subject can't be hidden with inline PGP, so callers should keep it generic.
/// </summary>
public sealed class ProtonMailEncryptor(HttpClient httpClient, ILogger<ProtonMailEncryptor> logger)
{
    /// <summary>Proton's HKP-style keyserver. Only holds keys for Proton-managed addresses.</summary>
    private const string LookupUrl = "https://api.protonmail.ch/pks/lookup?op=get&search=";

    /// <summary>Successful key lookups are cached this long. Misses aren't cached.</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (string ArmoredKey, DateTime ExpiresAt)> _keyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns an ASCII-armored PGP message if the address has a Proton key, otherwise null so the
    /// caller can skip the recipient rather than mail them in the clear.
    /// </summary>
    public async Task<string?> TryEncryptAsync(string address, string plaintext, CancellationToken cancellationToken)
    {
        string? armoredKey = await TryGetPublicKeyAsync(address, cancellationToken);
        if (armoredKey is null)
        {
            return null;
        }

        try
        {
            return Encrypt(plaintext, armoredKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to PGP-encrypt a Molly alert for a Proton recipient");
            return null;
        }
    }

    private async Task<string?> TryGetPublicKeyAsync(string address, CancellationToken cancellationToken)
    {
        if (_keyCache.TryGetValue(address, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.ArmoredKey;
        }

        try
        {
            using var response = await httpClient.GetAsync($"{LookupUrl}{Uri.EscapeDataString(address)}", cancellationToken);

            // A non-Proton address is a 404, which means the recipient is skipped.
            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("BEGIN PGP PUBLIC KEY BLOCK", StringComparison.Ordinal))
                {
                    // Only successful lookups are cached. Sends are already rate limited, so a miss
                    // re-querying every time is cheap and avoids pinning a stale "no key" result.
                    _keyCache[address] = (body, DateTime.UtcNow.Add(CacheDuration));
                    return body;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to look up a Proton key");
        }

        return null;
    }

    /// <summary>Produces an inline PGP message (AES-256, integrity protected) for the given key.</summary>
    public static string Encrypt(string plaintext, string armoredPublicKey)
    {
        PgpPublicKey encryptionKey = GetEncryptionKey(armoredPublicKey);

        byte[] literal = ToLiteralData(plaintext);

        var generator = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Aes256, withIntegrityPacket: true, new SecureRandom());

        generator.AddMethod(encryptionKey);

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        using (Stream encrypted = generator.Open(armored, literal.Length))
        {
            encrypted.Write(literal);
        }

        return Encoding.ASCII.GetString(output.ToArray());
    }

    private static byte[] ToLiteralData(string plaintext)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);

        using var buffer = new MemoryStream();
        var literalGenerator = new PgpLiteralDataGenerator();
        using (Stream literal = literalGenerator.Open(buffer, PgpLiteralData.Utf8, "", bytes.Length, DateTime.UtcNow))
        {
            literal.Write(bytes);
        }

        return buffer.ToArray();
    }

    /// <summary>Picks a usable encryption subkey. Proton keys sign with the primary and encrypt with a subkey.</summary>
    private static PgpPublicKey GetEncryptionKey(string armoredPublicKey)
    {
        using Stream keyStream = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.ASCII.GetBytes(armoredPublicKey)));
        var bundle = new PgpPublicKeyRingBundle(keyStream);

        foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
        {
            foreach (PgpPublicKey key in ring.GetPublicKeys())
            {
                if (key.IsEncryptionKey && !key.HasRevocation())
                {
                    return key;
                }
            }
        }

        throw new InvalidOperationException("No usable PGP encryption key was found.");
    }
}
