using System.Security.Cryptography;
using System.Text;
using MihuBot.Helpers.Crypto;

namespace MihuBot.Tests.Helpers;

/// <summary>Test vectors from the XAES-256-GCM specification, https://c2sp.org/XAES-256-GCM.</summary>
public sealed class XAesGcmTests
{
    private static readonly byte[] s_specNonce = "ABCDEFGHIJKLMNOPQRSTUVWX"u8.ToArray();
    private static readonly byte[] s_specPlaintext = "XAES-256-GCM"u8.ToArray();

    [Theory]
    // Key, associated data, expected ciphertext || tag. The first vector has MSB(L) = 0, the second MSB(L) = 1,
    // so between them they cover both branches of the CMAC subkey derivation.
    [InlineData(
        "0101010101010101010101010101010101010101010101010101010101010101",
        "",
        "ce546ef63c9cc60765923609b33a9a1974e96e52daf2fcf7075e2271")]
    [InlineData(
        "0303030303030303030303030303030303030303030303030303030303030303",
        "c2sp.org/XAES-256-GCM",
        "986ec1832593df5443a179437fd083bf3fdb41abd740a21f71eb769d")]
    public void SpecTestVectors_Match(string keyHex, string associatedData, string expectedHex)
    {
        byte[] key = Convert.FromHexString(keyHex);
        byte[] aad = Encoding.ASCII.GetBytes(associatedData);

        byte[] ciphertext = new byte[s_specPlaintext.Length];
        byte[] tag = new byte[XAesGcm.TagSizeInBytes];

        using var aead = new XAesGcm(key);
        aead.Encrypt(s_specNonce, s_specPlaintext, ciphertext, tag, aad);

        Assert.Equal(expectedHex, Convert.ToHexString([.. ciphertext, .. tag]).ToLowerInvariant());

        byte[] roundTripped = new byte[s_specPlaintext.Length];
        aead.Decrypt(s_specNonce, ciphertext, tag, roundTripped, aad);

        Assert.Equal(s_specPlaintext, roundTripped);
    }

    [Fact]
    public void Decrypt_WithTheWrongAssociatedData_Throws()
    {
        using var aead = NewAead();

        byte[] ciphertext = new byte[s_specPlaintext.Length];
        byte[] tag = new byte[XAesGcm.TagSizeInBytes];
        aead.Encrypt(s_specNonce, s_specPlaintext, ciphertext, tag, "expected"u8);

        Assert.ThrowsAny<CryptographicException>(() =>
            aead.Decrypt(s_specNonce, ciphertext, tag, new byte[s_specPlaintext.Length], "attacker"u8));
    }

    [Fact]
    public void Decrypt_WithADifferentNonceHalf_Throws()
    {
        using var aead = NewAead();

        byte[] ciphertext = new byte[s_specPlaintext.Length];
        byte[] tag = new byte[XAesGcm.TagSizeInBytes];
        aead.Encrypt(s_specNonce, s_specPlaintext, ciphertext, tag);

        // Both halves of the nonce have to be authenticated: the first feeds the key derivation,
        // the second is the AES-GCM nonce.
        foreach (int index in (int[])[0, 11, 12, 23])
        {
            byte[] nonce = (byte[])s_specNonce.Clone();
            nonce[index] ^= 0x01;

            Assert.ThrowsAny<CryptographicException>(() =>
                aead.Decrypt(nonce, ciphertext, tag, new byte[s_specPlaintext.Length]));
        }
    }

    [Fact]
    public void DifferentNonceHalves_DeriveDifferentKeystreams()
    {
        using var aead = NewAead();

        // Changing only the derivation half must change the ciphertext, proving the subkey depends on it.
        byte[] otherNonce = (byte[])s_specNonce.Clone();
        otherNonce[0] ^= 0x01;

        byte[] first = new byte[s_specPlaintext.Length];
        byte[] second = new byte[s_specPlaintext.Length];
        byte[] tag = new byte[XAesGcm.TagSizeInBytes];

        aead.Encrypt(s_specNonce, s_specPlaintext, first, tag);
        aead.Encrypt(otherNonce, s_specPlaintext, second, tag);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void KeysOfTheWrongLength_AreRejected()
    {
        foreach (int length in (int[])[0, 16, 24, 31, 33])
        {
            Assert.Throws<ArgumentException>(() => new XAesGcm(new byte[length]));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(1024)]
    public void Combined_RoundTrips(int plaintextLength)
    {
        using var aead = NewAead();

        byte[] plaintext = RandomNumberGenerator.GetBytes(plaintextLength);

        byte[] message = aead.Encrypt(plaintext);

        // The message carries its own nonce and tag on top of the plaintext.
        Assert.Equal(plaintextLength + XAesGcm.CombinedOverheadInBytes, message.Length);

        Assert.True(aead.TryDecrypt(message, out byte[]? roundTripped));
        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void Combined_RoundTripsWithAssociatedData()
    {
        using var aead = NewAead();

        byte[] message = aead.Encrypt(s_specPlaintext, "context"u8);

        Assert.True(aead.TryDecrypt(message, out byte[]? roundTripped, "context"u8));
        Assert.Equal(s_specPlaintext, roundTripped);

        // The same message must not open under different associated data.
        Assert.False(aead.TryDecrypt(message, out _, "other"u8));
    }

    [Fact]
    public void Combined_EncryptUsesAFreshNonceEachTime()
    {
        using var aead = NewAead();

        byte[] first = aead.Encrypt(s_specPlaintext);
        byte[] second = aead.Encrypt(s_specPlaintext);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Combined_TryDecryptRejectsTamperedMessages()
    {
        using var aead = NewAead();

        byte[] message = aead.Encrypt(s_specPlaintext);

        for (int i = 0; i < message.Length; i++)
        {
            byte[] tampered = (byte[])message.Clone();
            tampered[i] ^= 0x01;

            Assert.False(aead.TryDecrypt(tampered, out _));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(XAesGcm.NonceSizeInBytes)]
    [InlineData(XAesGcm.CombinedOverheadInBytes - 1)]
    public void Combined_TryDecryptRejectsMessagesTooShortToHoldANonceAndTag(int length)
    {
        using var aead = NewAead();

        Assert.False(aead.TryDecrypt(new byte[length], out _));
    }

    [Fact]
    public void Combined_TryDecryptRejectsMessagesFromAnotherKey()
    {
        using var aead = NewAead();
        using var other = NewAead();

        byte[] message = aead.Encrypt(s_specPlaintext);

        Assert.False(other.TryDecrypt(message, out _));
    }

    [Fact]
    public void NoncesOfTheWrongLength_AreRejected()
    {
        using var aead = NewAead();

        foreach (int length in (int[])[0, 12, 16, 23, 25])
        {
            Assert.Throws<ArgumentException>(() =>
                aead.Encrypt(new byte[length], s_specPlaintext, new byte[s_specPlaintext.Length], new byte[XAesGcm.TagSizeInBytes]));
        }
    }

    private static XAesGcm NewAead() => new(RandomNumberGenerator.GetBytes(XAesGcm.KeySizeInBytes));
}
