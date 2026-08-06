using System.Text;
using MihuBot.Molly;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace MihuBot.Tests.Molly;

public sealed class ProtonMailEncryptorTests
{
    // A throw-away Curve25519 (Ed25519 primary + ECDH subkey) keypair, matching Proton's key layout.
    // Generated with GnuPG solely for these tests - it protects nothing real.
    private const string PublicKey =
        """
        -----BEGIN PGP PUBLIC KEY BLOCK-----

        mDMEanUKhRYJKwYBBAHaRw8BAQdAxnXpWqapeiHQZEJC77/ZAjHQ+h1zy7tRnDOm
        fyBTe4m0IU1vbGx5IFRlc3QgPG1vbGx5LXRlc3RAcHJvdG9uLm1lPoiQBBMWCgA4
        FiEE1ZCOBEAJ+6W6aHigDHQBchQ+t+EFAmp1CoUCGwEFCwkIBwIGFQoJCAsCBBYC
        AwECHgECF4AACgkQDHQBchQ+t+GVAAD/W0N0Cx4GzZy3Yexgt2e4udt1EE++IImM
        sCUtXuYTsqsBAN8lXuXxZmKeXbb8+IGdZgnu2GHq4XdythFDvoJR1mkNuDgEanUK
        hRIKKwYBBAGXVQEFAQEHQLXP0+l9SsUo1pF6CdDlOrrfKl1p8QgCDqAR4iUPCrgr
        AwEIB4h4BBgWCgAgFiEE1ZCOBEAJ+6W6aHigDHQBchQ+t+EFAmp1CoUCGwwACgkQ
        DHQBchQ+t+E2fwEA0uFrPvk8YZ58Anh2B0J0SXCu9PFAfiVScNI9f25frvEA/iHk
        +dAWBuK+U146MJQDeebY4rF+S1amR0F7k5MVGTEL
        =Kb63
        -----END PGP PUBLIC KEY BLOCK-----
        """;

    private const string PrivateKey =
        """
        -----BEGIN PGP PRIVATE KEY BLOCK-----

        lFgEanUKhRYJKwYBBAHaRw8BAQdAxnXpWqapeiHQZEJC77/ZAjHQ+h1zy7tRnDOm
        fyBTe4kAAQD5a1Z9Iqosjocod0R6PaGd2052xy4wma9DOz7WzXAFGg4XtCFNb2xs
        eSBUZXN0IDxtb2xseS10ZXN0QHByb3Rvbi5tZT6IkAQTFgoAOBYhBNWQjgRACful
        umh4oAx0AXIUPrfhBQJqdQqFAhsBBQsJCAcCBhUKCQgLAgQWAgMBAh4BAheAAAoJ
        EAx0AXIUPrfhlQAA/1tDdAseBs2ct2HsYLdnuLnbdRBPviCJjLAlLV7mE7KrAQDf
        JV7l8WZinl22/PiBnWYJ7thh6uF3crYRQ76CUdZpDZxdBGp1CoUSCisGAQQBl1UB
        BQEBB0C1z9PpfUrFKNaRegnQ5Tq63ypdafEIAg6gEeIlDwq4KwMBCAcAAP9Lc8RI
        QuP/cisU5zVoxeSd7UpKb1Fz3RtdDYWc0naxUBDoiHgEGBYKACAWIQTVkI4EQAn7
        pbpoeKAMdAFyFD634QUCanUKhQIbDAAKCRAMdAFyFD634TZ/AQDS4Ws++TxhnnwC
        eHYHQnRJcK708UB+JVJw0j1/bl+u8QD+IeT50BYG4r5TXjowlAN55tjisX5LVqZH
        QXuTkxUZMQs=
        =kwyn
        -----END PGP PRIVATE KEY BLOCK-----
        """;

    [Fact]
    public void Encrypt_ProducesArmoredMessage()
    {
        string armored = ProtonMailEncryptor.Encrypt("hello", PublicKey);

        Assert.StartsWith("-----BEGIN PGP MESSAGE-----", armored);
        Assert.Contains("-----END PGP MESSAGE-----", armored);
    }

    [Theory]
    [InlineData("Molly alert: the device was locked remotely.")]
    [InlineData("")]
    [InlineData("Ünïcödé ☕ multi\nline\nbody")]
    public void Encrypt_RoundTripsThroughTheRecipientsPrivateKey(string plaintext)
    {
        string armored = ProtonMailEncryptor.Encrypt(plaintext, PublicKey);

        Assert.Equal(plaintext, Decrypt(armored));
    }

    [Fact]
    public void Encrypt_ThrowsWhenNoEncryptionKeyIsPresent()
    {
        Assert.Throws<InvalidOperationException>(() => ProtonMailEncryptor.Encrypt("hi", "not a key"));
    }

    /// <summary>Decrypts with BouncyCastle to prove the output is valid, standards-compliant OpenPGP.</summary>
    private static string Decrypt(string armoredMessage)
    {
        var secretKeys = new PgpSecretKeyRingBundle(
            PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.ASCII.GetBytes(PrivateKey))));

        using Stream input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.ASCII.GetBytes(armoredMessage)));
        var encryptedList = (PgpEncryptedDataList)new PgpObjectFactory(input).NextPgpObject();

        var encryptedData = (PgpPublicKeyEncryptedData)encryptedList[0];
        PgpPrivateKey privateKey = secretKeys.GetSecretKey(encryptedData.KeyId).ExtractPrivateKey([]);

        using Stream clear = encryptedData.GetDataStream(privateKey);
        var literal = (PgpLiteralData)new PgpObjectFactory(clear).NextPgpObject();

        using var buffer = new MemoryStream();
        literal.GetInputStream().CopyTo(buffer);

        Assert.True(encryptedData.Verify(), "The message integrity packet (MDC) did not verify.");

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
