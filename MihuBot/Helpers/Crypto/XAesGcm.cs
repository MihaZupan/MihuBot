using System.Diagnostics.CodeAnalysis;
using System.Numerics.Tensors;
using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Helpers.Crypto;

/// <summary>
/// XAES-256-GCM: AES-256-GCM extended to a 192-bit nonce, as specified by https://c2sp.org/XAES-256-GCM.
/// </summary>
/// <remarks>
/// <para>
/// Plain AES-GCM only takes a 96-bit nonce, which caps a single key at ~2^32 messages before the
/// birthday bound on randomly generated nonces becomes a concern. A repeated (key, nonce) pair in GCM
/// is catastrophic: it leaks the GHASH subkey and lets an attacker forge arbitrary messages.
/// </para>
/// <para>
/// XAES-256-GCM removes that limit by splitting a 192-bit nonce in half. The first 96 bits are run
/// through a NIST SP 800-108r1 counter-mode KDF (instantiated with CMAC-AES256) to derive a per-message
/// subkey, and the remaining 96 bits are used as the AES-GCM nonce. Reuse now requires <em>both</em>
/// halves to collide, so random nonces are safe for 2^80 messages at a collision risk of 2^-32.
/// </para>
/// <para>
/// The cost is three extra AES invocations per message, one of which (<c>L</c>/<c>K1</c> below) is
/// precomputed once per key.
/// </para>
/// <para>
/// Like AES-GCM, this is neither nonce misuse-resistant nor key-committing.
/// </para>
/// </remarks>
public sealed class XAesGcm : IDisposable
{
    public const int KeySizeInBytes = 32;
    public const int NonceSizeInBytes = 24;
    public const int TagSizeInBytes = 16;

    /// <summary>
    /// Bytes a <see cref="Encrypt(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> message adds on top of the
    /// plaintext: the prepended nonce and the appended tag.
    /// </summary>
    public const int CombinedOverheadInBytes = NonceSizeInBytes + TagSizeInBytes;

    private const int BlockSize = 16;
    /// <summary>The first half of the nonce feeds the KDF, the second half is the AES-GCM nonce.</summary>
    private const int DerivationNonceSize = NonceSizeInBytes / 2; // 12, matching AesGcm.NonceByteSizes.MaxSize
    private const byte Label = 0x58; // ASCII 'X'

    private readonly byte[] _key;

    /// <summary>The CMAC subkey, which only depends on the key and so is computed once.</summary>
    private readonly byte[] _cmacSubkey = new byte[BlockSize];

    public XAesGcm(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeInBytes)
        {
            throw new ArgumentException($"Key must be {KeySizeInBytes} bytes.", nameof(key));
        }

        _key = key.ToArray();

        // Step 1: L = AES-256(K, 0^128)
        Span<byte> zeroBlock = stackalloc byte[BlockSize];
        zeroBlock.Clear();

        Span<byte> l = stackalloc byte[BlockSize];
        using (Aes aes = CreateAes())
        {
            aes.EncryptEcb(zeroBlock, l, PaddingMode.None);
        }

        // Step 2: K1 = L << 1, xored with the GF(2^128) reduction polynomial if L overflowed.
        byte carry = 0;
        for (int i = BlockSize - 1; i >= 0; i--)
        {
            _cmacSubkey[i] = (byte)((l[i] << 1) | carry);
            carry = (byte)(l[i] >>> 7);
        }

        // Applied via a mask rather than an 'if', so the timing doesn't depend on the key-derived MSB.
        byte reductionMask = (byte)(0 - (l[0] >>> 7)); // 0xFF when the MSB was set, 0x00 otherwise.
        _cmacSubkey[BlockSize - 1] ^= (byte)(0x87 & reductionMask);

        CryptographicOperations.ZeroMemory(l);
    }

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
    {
        Span<byte> subkey = stackalloc byte[KeySizeInBytes];
        try
        {
            DeriveSubkey(nonce, subkey);

            using var aesGcm = new AesGcm(subkey, TagSizeInBytes);
            aesGcm.Encrypt(nonce.Slice(DerivationNonceSize), plaintext, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }
    }

    /// <exception cref="CryptographicException">The tag doesn't match.</exception>
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        Span<byte> subkey = stackalloc byte[KeySizeInBytes];
        try
        {
            DeriveSubkey(nonce, subkey);

            using var aesGcm = new AesGcm(subkey, TagSizeInBytes);
            aesGcm.Decrypt(nonce.Slice(DerivationNonceSize), ciphertext, tag, plaintext, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }
    }

    /// <summary>
    /// Encrypts into a single self-contained message laid out as
    /// <c>nonce (24) || ciphertext || tag (16)</c>, with a fresh random nonce.
    /// </summary>
    /// <remarks>
    /// The 192-bit nonce is what makes random generation safe here, see the type-level remarks.
    /// Pair with <see cref="TryDecrypt"/>.
    /// </remarks>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        byte[] message = new byte[NonceSizeInBytes + plaintext.Length + TagSizeInBytes];
        Span<byte> span = message;

        Span<byte> nonce = span.Slice(0, NonceSizeInBytes);
        RandomNumberGenerator.Fill(nonce);

        Encrypt(nonce, plaintext, span.Slice(NonceSizeInBytes, plaintext.Length), span.Slice(NonceSizeInBytes + plaintext.Length), associatedData);

        return message;
    }

    /// <summary>
    /// Decrypts a message produced by <see cref="Encrypt(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
    /// Fails - rather than throwing - for anything too short to hold a nonce and tag, and for a tag
    /// that doesn't match (tampered, or encrypted under a different key).
    /// </summary>
    public bool TryDecrypt(ReadOnlySpan<byte> message, [NotNullWhen(true)] out byte[]? plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        plaintext = null;

        if (message.Length < CombinedOverheadInBytes)
        {
            return false;
        }

        int plaintextLength = message.Length - CombinedOverheadInBytes;
        byte[] result = new byte[plaintextLength];

        try
        {
            Decrypt(
                message.Slice(0, NonceSizeInBytes),
                message.Slice(NonceSizeInBytes, plaintextLength),
                message.Slice(NonceSizeInBytes + plaintextLength),
                result,
                associatedData);
        }
        catch (CryptographicException)
        {
            return false;
        }

        plaintext = result;
        return true;
    }

    /// <summary>Kₓ = AES-256(K, M1 ^ K1) || AES-256(K, M2 ^ K1), where Mᵢ = 0x00 || i || 'X' || 0x00 || nonce[..12].</summary>
    private void DeriveSubkey(ReadOnlySpan<byte> nonce, Span<byte> subkey)
    {
        if (nonce.Length != NonceSizeInBytes)
        {
            throw new ArgumentException($"Nonce must be {NonceSizeInBytes} bytes.", nameof(nonce));
        }

        ReadOnlySpan<byte> context = nonce.Slice(0, DerivationNonceSize);

        Span<byte> message = stackalloc byte[BlockSize];

        using Aes aes = CreateAes();

        for (int counter = 1; counter <= 2; counter++)
        {
            message[0] = 0x00;
            message[1] = (byte)counter;
            message[2] = Label;
            message[3] = 0x00;
            context.CopyTo(message.Slice(4));

            TensorPrimitives.Xor(message, _cmacSubkey, message);

            // The message is exactly one full block, so CMAC reduces to a single AES invocation.
            aes.EncryptEcb(message, subkey.Slice((counter - 1) * BlockSize, BlockSize), PaddingMode.None);
        }

        CryptographicOperations.ZeroMemory(message);
    }

    private Aes CreateAes()
    {
        Aes aes = Aes.Create();
        aes.Key = _key;
        return aes;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        CryptographicOperations.ZeroMemory(_cmacSubkey);
    }
}
