using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

public sealed class MollyRateLimiterTests
{
    /// <summary>Matches MollyRateLimiter.DefaultBurst.</summary>
    private const int DefaultBurst = 10;

    private static readonly TimeSpan TestCooldown = TimeSpan.FromMilliseconds(200);

    private static MollyRateLimiter CreateLimiter(int burst = 5) => new(TestCooldown, burst);

    private static HttpContext Context(string connectionIp, string? realIp = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(connectionIp);

        if (realIp is not null)
        {
            context.Request.Headers["X-Real-IP"] = realIp;
        }

        return context;
    }

    /// <summary>Sends requests until one is rejected, and returns how many were allowed.</summary>
    private static int Exhaust(MollyRateLimiter limiter, Func<HttpContext> context, int limit = 100)
    {
        int allowed = 0;

        while (allowed < limit && limiter.TryEnter(context(), out _))
        {
            allowed++;
        }

        return allowed;
    }

    [Fact]
    public void DefaultBurst_AllowsANormalUsageBurst()
    {
        var limiter = new MollyRateLimiter();

        int allowed = Exhaust(limiter, () => Context("10.0.0.1"));

        Assert.Equal(DefaultBurst, allowed);
    }

    [Fact]
    public void Burst_IsExhaustedThenThrottled()
    {
        MollyRateLimiter limiter = CreateLimiter(burst: 5);

        Assert.Equal(5, Exhaust(limiter, () => Context("10.0.0.1")));
        Assert.False(limiter.TryEnter(Context("10.0.0.1"), out _));
    }

    [Fact]
    public void Throttling_ReportsTheCooldownAsRetryAfter()
    {
        MollyRateLimiter limiter = CreateLimiter();
        Exhaust(limiter, () => Context("10.0.0.1"));

        Assert.False(limiter.TryEnter(Context("10.0.0.1"), out TimeSpan retryAfter));
        Assert.Equal(TestCooldown, retryAfter);
    }

    [Fact]
    public void AllowedRequests_ReportNoRetryAfter()
    {
        MollyRateLimiter limiter = CreateLimiter();

        Assert.True(limiter.TryEnter(Context("10.0.0.1"), out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void Bucket_PartiallyRefillsOverTime()
    {
        var time = new FakeTimeProvider();
        var limiter = new MollyRateLimiter(TestCooldown, 5, time);
        Exhaust(limiter, () => Context("10.0.0.1"));

        Assert.False(limiter.TryEnter(Context("10.0.0.1"), out _));

        time.Advance(TestCooldown * 2);

        int allowed = Exhaust(limiter, () => Context("10.0.0.1"));

        Assert.Equal(2, allowed);
    }

    [Fact]
    public void Bucket_FullyRefillsAfterBeingIdle()
    {
        const int Burst = 5;

        var time = new FakeTimeProvider();
        var limiter = new MollyRateLimiter(TestCooldown, Burst, time);
        Exhaust(limiter, () => Context("10.0.0.1"));

        time.Advance(TestCooldown * (Burst + 1));

        Assert.Equal(Burst, Exhaust(limiter, () => Context("10.0.0.1")));
    }

    [Fact]
    public void Limits_AreTrackedPerConnectionIp()
    {
        MollyRateLimiter limiter = CreateLimiter();
        Exhaust(limiter, () => Context("10.0.0.1"));

        Assert.False(limiter.TryEnter(Context("10.0.0.1"), out _));
        Assert.True(limiter.TryEnter(Context("10.0.0.2"), out _));
    }

    [Fact]
    public void RealIpHeader_TakesPrecedenceOverTheConnectionIp()
    {
        MollyRateLimiter limiter = CreateLimiter();
        Exhaust(limiter, () => Context("10.0.0.1", realIp: "203.0.113.10"));

        Assert.False(limiter.TryEnter(Context("10.0.0.1", realIp: "203.0.113.10"), out _));

        // A different device behind the same proxy must not be affected ...
        Assert.True(limiter.TryEnter(Context("10.0.0.1", realIp: "203.0.113.11"), out _));

        // ... and the shared proxy address itself must never have been charged.
        Assert.True(limiter.TryEnter(Context("10.0.0.1"), out _));
    }

    [Fact]
    public void RealIp_IsTrackedAcrossProxies()
    {
        MollyRateLimiter limiter = CreateLimiter();
        Exhaust(limiter, () => Context("10.0.0.1", realIp: "203.0.113.20"));

        Assert.False(limiter.TryEnter(Context("10.0.0.99", realIp: "203.0.113.20"), out _));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void MalformedRealIp_FallsBackToTheConnectionIp(string realIp)
    {
        MollyRateLimiter limiter = CreateLimiter();
        Exhaust(limiter, () => Context("198.51.100.7", realIp: realIp));

        Assert.False(limiter.TryEnter(Context("198.51.100.7", realIp: realIp), out _));
    }

    [Fact]
    public void MissingConnectionIp_DoesNotThrow()
    {
        MollyRateLimiter limiter = CreateLimiter();
        var context = new DefaultHttpContext();

        Assert.True(limiter.TryEnter(context, out _));
    }
}
