using System.Security.Cryptography;
using System.Text;
using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

public sealed class MollyAppSignatureTests : IClassFixture<MollyServiceFixture>
{
    private const string Uri = "/api/molly/login";

    private static readonly byte[] s_body = Encoding.UTF8.GetBytes("""{"keyHash":"AAAA"}""");

    private readonly MollyServiceFixture _fixture;
    private MollyService Molly => _fixture.Service;

    public MollyAppSignatureTests(MollyServiceFixture fixture) => _fixture = fixture;

    private static string ValidSignature => MollyTestKeys.Sign(Uri, s_body);

    [Fact]
    public void ValidSignature_IsAccepted()
    {
        Assert.True(Molly.VerifyAppSignature(ValidSignature, Uri, s_body));
    }

    [Fact]
    public void SignatureFromADifferentSecret_IsRejected()
    {
        string signature = MollyTestKeys.Sign(Uri, s_body, appSecret: MollyTestKeys.OtherAppSecret);

        Assert.False(Molly.VerifyAppSignature(signature, Uri, s_body));
    }

    [Fact]
    public void SignatureForADifferentUri_IsRejected()
    {
        string signature = MollyTestKeys.Sign("/api/molly/ping", s_body);

        Assert.False(Molly.VerifyAppSignature(signature, Uri, s_body));
    }

    [Fact]
    public void SignatureForADifferentBody_IsRejected()
    {
        string signature = MollyTestKeys.Sign(Uri, "{}"u8);

        Assert.False(Molly.VerifyAppSignature(signature, Uri, s_body));
    }

    [Fact]
    public void TamperedBody_IsRejected()
    {
        byte[] tampered = Encoding.UTF8.GetBytes("""{"keyHash":"AAAB"}""");

        Assert.False(Molly.VerifyAppSignature(ValidSignature, Uri, tampered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a signature")]
    public void MissingOrMalformedSignature_IsRejected(string? signature)
    {
        Assert.False(Molly.VerifyAppSignature(signature, Uri, s_body));
    }

    [Theory]
    [InlineData(32)]    // Too short.
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(96)]    // Too long.
    public void SignatureOfTheWrongLength_IsRejected(int length)
    {
        string signature = Convert.ToBase64String(new byte[length]);

        Assert.False(Molly.VerifyAppSignature(signature, Uri, s_body));
    }

    [Fact]
    public void HexEncodedSignature_IsRejected()
    {
        string hex = Convert.ToHexString(Convert.FromBase64String(ValidSignature));

        Assert.False(Molly.VerifyAppSignature(hex, Uri, s_body));
    }

    [Fact]
    public void SignatureWithWhitespace_IsRejected()
    {
        string signature = ValidSignature;

        Assert.False(Molly.VerifyAppSignature(" " + signature, Uri, s_body));
        Assert.False(Molly.VerifyAppSignature(signature + " ", Uri, s_body));
        Assert.False(Molly.VerifyAppSignature(signature + "\n", Uri, s_body));
        Assert.False(Molly.VerifyAppSignature(signature.Insert(4, "\n"), Uri, s_body));
    }

    [Fact]
    public void TruncatedSignature_IsRejected()
    {
        Assert.False(Molly.VerifyAppSignature(ValidSignature[..^4], Uri, s_body));
    }

    [Fact]
    public void EmptyBody_IsSignedAndVerifiedConsistently()
    {
        string signature = MollyTestKeys.Sign(Uri, ReadOnlySpan<byte>.Empty);

        Assert.True(Molly.VerifyAppSignature(signature, Uri, ReadOnlySpan<byte>.Empty));
        Assert.False(Molly.VerifyAppSignature(signature, Uri, s_body));
    }

    [Fact]
    public void SignatureCoversTheQueryString()
    {
        string signature = MollyTestKeys.Sign("/api/molly/login?a=1", s_body);

        Assert.True(Molly.VerifyAppSignature(signature, "/api/molly/login?a=1", s_body));
        Assert.False(Molly.VerifyAppSignature(signature, "/api/molly/login?a=2", s_body));
    }

    [Fact]
    public void RandomSignatures_AreRejected()
    {
        for (int i = 0; i < 32; i++)
        {
            string signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

            Assert.False(Molly.VerifyAppSignature(signature, Uri, s_body));
        }
    }
}
