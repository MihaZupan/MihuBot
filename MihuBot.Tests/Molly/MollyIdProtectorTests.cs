using System.Security.Cryptography;
using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

public sealed class MollyIdProtectorTests
{
    private readonly MollyIdProtector _protector = new(MollyTestKeys.DatabaseKeyBytes);

    [Fact]
    public void ProtectedIds_RoundTrip()
    {
        Guid id = Guid.NewGuid();

        Assert.True(_protector.TryUnprotect(_protector.Protect(id), out Guid unprotected));
        Assert.Equal(id, unprotected);
    }

    [Fact]
    public void Tokens_DoNotRevealTheId()
    {
        Guid id = Guid.NewGuid();
        string token = _protector.Protect(id);

        Assert.DoesNotContain(id.ToString(), token, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(id.ToString("N"), token, StringComparison.OrdinalIgnoreCase);

        // The raw bytes must not appear either.
        Assert.False(Convert.FromBase64String(token).AsSpan().IndexOf(id.ToByteArray()) >= 0);
    }

    [Fact]
    public void TheSameId_ProducesADifferentTokenEachTime()
    {
        Guid id = Guid.NewGuid();

        string first = _protector.Protect(id);
        string second = _protector.Protect(id);

        Assert.NotEqual(first, second);
        Assert.Equal(id, Unprotect(first));
        Assert.Equal(id, Unprotect(second));
    }

    [Fact]
    public void TokensFromAnotherServerKey_AreRejected()
    {
        // The key is derived from the server key, so tokens don't cross deployments with different keys.
        string token = new MollyIdProtector(MollyTestKeys.OtherDatabaseKeyBytes).Protect(Guid.NewGuid());

        Assert.False(_protector.TryUnprotect(token, out _));
    }

    [Fact]
    public void TokensSurviveARestart_WhenTheServerKeyIsTheSame()
    {
        // A new process with the same server key has to keep accepting already issued tokens,
        // so clients that aren't logged in can keep pinging across a restart.
        Guid id = Guid.NewGuid();
        string token = new MollyIdProtector(MollyTestKeys.DatabaseKeyBytes).Protect(id);

        Assert.True(_protector.TryUnprotect(token, out Guid unprotected));
        Assert.Equal(id, unprotected);
    }

    [Fact]
    public void TheKey_MustBeLongEnough()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MollyIdProtector(new byte[31]));
    }

    [Fact]
    public void TamperedTokens_AreRejected()
    {
        string token = _protector.Protect(Guid.NewGuid());
        byte[] bytes = Convert.FromBase64String(token);

        // Flipping any single bit has to invalidate the MAC.
        for (int i = 0; i < bytes.Length; i++)
        {
            byte[] tampered = (byte[])bytes.Clone();
            tampered[i] ^= 0x01;

            Assert.False(_protector.TryUnprotect(Convert.ToBase64String(tampered), out _), $"Byte {i} was not covered by the MAC.");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void MalformedTokens_AreRejected(string? token)
    {
        Assert.False(_protector.TryUnprotect(token, out _));
    }

    [Fact]
    public void Tokens_HaveTheExpectedLayout()
    {
        // 24 byte XAES-256-GCM nonce + 16 byte encrypted Guid + 16 byte tag.
        byte[] bytes = Convert.FromBase64String(_protector.Protect(Guid.NewGuid()));

        Assert.Equal(24 + 16 + 16, bytes.Length);
    }

    [Fact]
    public void Nonces_AreNotReusedAcrossTokens()
    {
        HashSet<string> nonces = [];

        for (int i = 0; i < 1000; i++)
        {
            byte[] bytes = Convert.FromBase64String(_protector.Protect(Guid.NewGuid()));

            Assert.True(nonces.Add(Convert.ToHexString(bytes.AsSpan(0, 24))));
        }
    }

    [Fact]
    public void TokensOfTheWrongLength_AreRejected()
    {
        foreach (int length in (int[])[1, 16, 32, 44, 55, 57, 64])
        {
            Assert.False(_protector.TryUnprotect(Convert.ToBase64String(new byte[length]), out _));
        }
    }

    [Fact]
    public void TruncatedOrPaddedTokens_AreRejected()
    {
        string token = _protector.Protect(Guid.NewGuid());

        Assert.False(_protector.TryUnprotect(token[..^4], out _));
        Assert.False(_protector.TryUnprotect(token + "AAAA", out _));
        Assert.False(_protector.TryUnprotect(" " + token, out _));
    }

    [Fact]
    public void RandomTokens_AreRejected()
    {
        // 24 byte nonce + 16 byte encrypted id + 16 byte tag.
        for (int i = 0; i < 64; i++)
        {
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(56));

            Assert.False(_protector.TryUnprotect(token, out _));
        }
    }

    [Fact]
    public void ManyIds_RoundTripIndependently()
    {
        Guid[] ids = [.. Enumerable.Range(0, 100).Select(_ => Guid.NewGuid())];
        string[] tokens = [.. ids.Select(_protector.Protect)];

        Assert.Equal(ids.Length, tokens.Distinct().Count());

        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(ids[i], Unprotect(tokens[i]));
        }
    }

    private Guid Unprotect(string token)
    {
        Assert.True(_protector.TryUnprotect(token, out Guid id));
        return id;
    }
}
