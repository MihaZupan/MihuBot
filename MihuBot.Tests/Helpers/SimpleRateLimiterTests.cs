using MihuBot.Helpers;

namespace MihuBot.Tests.Helpers;

public sealed class SimpleRateLimiterTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(200);

    /// <summary>Consumes requests until one is rejected, and returns how many were allowed.</summary>
    private static int Exhaust(SimpleRateLimiter limiter, int limit = 1000)
    {
        int allowed = 0;

        while (allowed < limit && limiter.TryEnter())
        {
            allowed++;
        }

        return allowed;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void Tolerance_IsAvailableUpFront(int maxTolerance)
    {
        var limiter = new SimpleRateLimiter(Cooldown, maxTolerance);

        Assert.Equal(maxTolerance, Exhaust(limiter));
    }

    [Fact]
    public void OnceExhausted_FurtherRequestsAreRejected()
    {
        var limiter = new SimpleRateLimiter(Cooldown, 3);
        Exhaust(limiter);

        Assert.False(limiter.TryEnter());
        Assert.False(limiter.TryEnter());
    }

    [Fact]
    public async Task RequestsAreEarnedBackOverTime()
    {
        var limiter = new SimpleRateLimiter(Cooldown, 5);
        Exhaust(limiter);

        Assert.False(limiter.TryEnter());

        await Task.Delay(Cooldown * 2);

        // Roughly one request is earned per cooldown, so not the whole allowance comes back.
        int allowed = Exhaust(limiter);

        Assert.InRange(allowed, 1, 4);
    }

    [Fact]
    public async Task AfterALongIdlePeriod_TheFullToleranceIsRestored()
    {
        const int MaxTolerance = 4;

        var limiter = new SimpleRateLimiter(Cooldown, MaxTolerance);
        Exhaust(limiter);

        await Task.Delay(Cooldown * (MaxTolerance + 1));

        Assert.Equal(MaxTolerance, Exhaust(limiter));
    }

    [Fact]
    public void Count_ConsumesMultipleAtOnce()
    {
        var limiter = new SimpleRateLimiter(Cooldown, 10);

        Assert.True(limiter.TryEnter(4));
        Assert.True(limiter.TryEnter(4));

        // Only two are left, so a batch of four can't be satisfied.
        Assert.False(limiter.TryEnter(4));
        Assert.True(limiter.TryEnter(2));
    }

    [Fact]
    public void CountLargerThanTolerance_IsRejected()
    {
        var limiter = new SimpleRateLimiter(Cooldown, 3);

        Assert.False(limiter.TryEnter(4));

        // The rejected request must not have consumed anything.
        Assert.Equal(3, Exhaust(limiter));
    }

    [Fact]
    public async Task ALongCooldown_DoesNotRegenerateEarly()
    {
        var limiter = new SimpleRateLimiter(TimeSpan.FromHours(1), 2);
        Exhaust(limiter);

        await Task.Delay(50);

        Assert.False(limiter.TryEnter());
    }

    [Fact]
    public void ConcurrentCallers_NeverExceedTheTolerance()
    {
        const int MaxTolerance = 100;

        // A long cooldown so that nothing regenerates while the test runs.
        var limiter = new SimpleRateLimiter(TimeSpan.FromHours(1), MaxTolerance);

        int allowed = 0;

        Parallel.For(0, 1000, _ =>
        {
            if (limiter.TryEnter())
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(MaxTolerance, allowed);
    }
}
