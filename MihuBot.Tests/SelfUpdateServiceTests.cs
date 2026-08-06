namespace MihuBot.Tests;

public class SelfUpdateServiceTests
{
    // Produced by `ssh-keygen -Y sign -n git` with the key below.
    private const string Signature =
        """
        -----BEGIN SSH SIGNATURE-----
        U1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAgJybKri1zVXKKuRfgZIO2tS83J/
        7ruD/w4I++6iVwQZoAAAADZ2l0AAAAAAAAAAZzaGE1MTIAAABTAAAAC3NzaC1lZDI1NTE5
        AAAAQBsSBv8NDlR0D9TjM73wiwNJPjAQtQ5DfWh3o2DWLs8+i+sJzjnPvmCAOBZ0zkyonk
        RZjpgaTmZ8dTcDYYgYpQM=
        -----END SSH SIGNATURE-----
        """;

    private const string PublicKey = "AAAAC3NzaC1lZDI1NTE5AAAAICcmyq4tc1VyirkX4GSDtrUvNyf+67g/8OCPvuolcEGa";

    [Fact]
    public void TryGetSshSignaturePublicKey_ExtractsTheSigningKey()
    {
        Assert.Equal(PublicKey, SelfUpdateService.TryGetSshSignaturePublicKey(Signature));
        Assert.Equal(PublicKey, SelfUpdateService.TryGetSshSignaturePublicKey(Signature.ReplaceLineEndings("\r\n")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a signature")]
    // A GPG signature - we only accept SSH ones.
    [InlineData("-----BEGIN PGP SIGNATURE-----\nabcd\n-----END PGP SIGNATURE-----")]
    // Valid armor, but the body isn't base64 / isn't an SSHSIG blob.
    [InlineData("-----BEGIN SSH SIGNATURE-----\n!!!!\n-----END SSH SIGNATURE-----")]
    [InlineData("-----BEGIN SSH SIGNATURE-----\nAAAA\n-----END SSH SIGNATURE-----")]
    public void TryGetSshSignaturePublicKey_ReturnsNullForInvalidSignatures(string? signature)
    {
        Assert.Null(SelfUpdateService.TryGetSshSignaturePublicKey(signature));
    }

    [Fact]
    public void TryGetSshSignaturePublicKey_ReturnsNullForTruncatedSignatures()
    {
        string body = string.Concat(Signature
            .Split('\n')
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal))
            .Select(line => line.Trim()));

        byte[] blob = Convert.FromBase64String(body);

        // Anything cut off before the end of the publickey field must be rejected.
        int keyEndOffset = "SSHSIG"u8.Length + 4 + 4 + Convert.FromBase64String(PublicKey).Length;

        for (int length = 1; length < keyEndOffset; length++)
        {
            string truncated = $"-----BEGIN SSH SIGNATURE-----\n{Convert.ToBase64String(blob.AsSpan(0, length))}\n-----END SSH SIGNATURE-----";
            Assert.Null(SelfUpdateService.TryGetSshSignaturePublicKey(truncated));
        }
    }
}
