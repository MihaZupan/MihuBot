using MihuBot.Helpers.RateLimiting;

namespace MihuBot.Tests.Helpers;

public sealed class TokenRateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EntireBudgetIsSharedAndNotRefundedWhenRequestStarts()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 1_000);
        await limiter.ReserveAsync(100, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task waiting = limiter.ReserveAsync(1, cancellation.Token);

        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task ParallelReservationsCannotExceedSharedBudget()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 90, queueLimit: 1_000);
        using var cancellation = new CancellationTokenSource();
        var requests = new Task<TokenRateLimiter.Reservation>[8];

        Parallel.For(0, requests.Length, i => requests[i] = limiter.ReserveAsync(30, cancellation.Token));

        Assert.Equal(3, requests.Count(t => t.IsCompletedSuccessfully));
        Assert.Equal(5, requests.Count(t => !t.IsCompleted));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(requests));
        Assert.Equal(3, requests.Count(t => t.IsCompletedSuccessfully));
        Assert.Equal(5, requests.Count(t => t.IsCanceled));
    }

    [Fact]
    public async Task BudgetReplenishmentUnblocksQueuedReservations()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 1_000, window: TimeSpan.FromMilliseconds(120));
        await limiter.ReserveAsync(100, CancellationToken.None);
        await limiter.ReserveAsync(100, CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RefundImmediatelyUnblocksQueuedReservations()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 100);
        TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(100, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(60, CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        reservation.Complete(40);
        await waiting.WaitAsync(TestTimeout);

        using var cancellation = new CancellationTokenSource();
        Task blocked = limiter.ReserveAsync(1, cancellation.Token);
        Assert.False(blocked.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task ZeroUsageRefundsEntireReservationAndDuplicateCompletionCannotRefundAgain()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 100);
        TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(100, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(100, CancellationToken.None);
        reservation.Complete(0);
        await waiting.WaitAsync(TestTimeout);

        Assert.Throws<InvalidOperationException>(() => reservation.Complete(0));

        using var cancellation = new CancellationTokenSource();
        Task blocked = limiter.ReserveAsync(1, cancellation.Token);
        Assert.False(blocked.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task NegativeUsageIsRejectedWithoutCompletingOrChangingCharge()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 100);
        TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(100, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(100, CancellationToken.None);

        Assert.Throws<ArgumentOutOfRangeException>(() => reservation.Complete(-1));
        Assert.False(waiting.IsCompleted);
        reservation.Complete(0);
        await waiting.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task HigherActualUsageBlocksStartsUntilOriginalReservationExpires()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new TokenRateLimiter(100, 100, Window, clock);
        TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(40, CancellationToken.None);
        clock.Advance(Window / 2);
        reservation.Complete(150);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(1, CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        clock.Advance(Window / 2 - TimeSpan.FromMilliseconds(1));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await waiting.WaitAsync(TestTimeout);
        await limiter.ReserveAsync(99, CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RefundsMustPayOffUnderestimatedDebtBeforeStartingQueuedWork()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new TokenRateLimiter(100, 100, Window, clock);
        TokenRateLimiter.Reservation underestimated = await limiter.ReserveAsync(50, CancellationToken.None);
        TokenRateLimiter.Reservation refundable = await limiter.ReserveAsync(50, CancellationToken.None);
        underestimated.Complete(120);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(1, CancellationToken.None);
        refundable.Complete(0);

        Assert.False(waiting.IsCompleted);
        clock.Advance(Window);
        await waiting.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task MultipleLargeUnderestimatesCannotOverflowTheCharge()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new TokenRateLimiter(100, 100, Window, clock);
        TokenRateLimiter.Reservation first = await limiter.ReserveAsync(50, CancellationToken.None);
        TokenRateLimiter.Reservation second = await limiter.ReserveAsync(50, CancellationToken.None);
        first.Complete(int.MaxValue);
        second.Complete(int.MaxValue);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(100, CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        clock.Advance(Window);
        await waiting.WaitAsync(TestTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public async Task CompletionAfterExpiryCannotCreditOrChargeLaterReservations(int actualTokens)
    {
        var clock = new ManualTimeProvider();
        using var limiter = new TokenRateLimiter(100, 100, Window, clock);
        TokenRateLimiter.Reservation expired = await limiter.ReserveAsync(100, CancellationToken.None);
        clock.Advance(Window);
        TokenRateLimiter.Reservation current = await limiter.ReserveAsync(100, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(100, CancellationToken.None);
        expired.Complete(actualTokens);

        Assert.False(waiting.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => expired.Complete(0));
        current.Complete(0);
        await waiting.WaitAsync(TestTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public async Task CompletionAtExpiryBeforeTimerRunsDoesNotAlterTheNextCharge(int actualTokens)
    {
        var clock = new ManualTimeProvider();
        using var limiter = new TokenRateLimiter(100, 100, Window, clock);
        TokenRateLimiter.Reservation expired = await limiter.ReserveAsync(100, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(100, CancellationToken.None);
        clock.Advance(Window, fireTimer: false);
        expired.Complete(actualTokens);
        TokenRateLimiter.Reservation current = await waiting.WaitAsync(TestTimeout);
        clock.FireTimerCallback();
        Task<TokenRateLimiter.Reservation> next = limiter.ReserveAsync(100, CancellationToken.None);

        Assert.False(next.IsCompleted);
        current.Complete(0);
        await next.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ReservationsExpireIndividuallyAndNotAtWindowBoundaries()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new TokenRateLimiter(100, 100, Window, clock);
        await limiter.ReserveAsync(40, CancellationToken.None);
        clock.Advance(Window / 2);
        await limiter.ReserveAsync(60, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> first = limiter.ReserveAsync(40, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> second = limiter.ReserveAsync(60, CancellationToken.None);
        clock.Advance(Window / 2 - TimeSpan.FromMilliseconds(1));

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await first.WaitAsync(TestTimeout);

        Assert.False(second.IsCompleted);
        clock.Advance(Window / 2);
        await second.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task RefundHonorsFifoAndCancellationUnblocksNextReservation()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 100);
        TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(100, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task head = limiter.ReserveAsync(60, cancellation.Token);
        Task<TokenRateLimiter.Reservation> next = limiter.ReserveAsync(40, CancellationToken.None);
        reservation.Complete(60);

        Assert.False(head.IsCompleted);
        Assert.False(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head);
        await next.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CancelledReservationDoesNotConsumeBudget()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 1_000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.ReserveAsync(1, cancellation.Token));
        await limiter.ReserveAsync(100, CancellationToken.None);
    }

    [Fact]
    public async Task CancelledQueuedReservationLeavesRemainingBudgetAvailable()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 1_000);
        await limiter.ReserveAsync(80, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task waiting = limiter.ReserveAsync(40, cancellation.Token);

        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await limiter.ReserveAsync(20, CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task NewReservationCannotBypassQueuedHead()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 100);
        await limiter.ReserveAsync(80, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task head = limiter.ReserveAsync(40, cancellation.Token);
        Task<TokenRateLimiter.Reservation> tail = limiter.ReserveAsync(20, CancellationToken.None);

        Assert.False(head.IsCompleted);
        Assert.False(tail.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head);
        await tail.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task QueueCapacityIsMeasuredInTokensAndCancellationReleasesCapacity()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 60);
        await limiter.ReserveAsync(100, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task first = limiter.ReserveAsync(40, cancellation.Token);
        Task second = limiter.ReserveAsync(20, cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => limiter.ReserveAsync(1, CancellationToken.None));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        using var nextCancellation = new CancellationTokenSource();
        Task next = limiter.ReserveAsync(60, nextCancellation.Token);
        Assert.False(next.IsCompleted);
        nextCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task OversizedReservationFailsExplicitlyWithoutConsumingBudget()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 1_000);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            limiter.ReserveAsync(101, CancellationToken.None));

        Assert.Contains("exceeds the window budget", error.Message, StringComparison.Ordinal);
        await limiter.ReserveAsync(100, CancellationToken.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveReservationsAreRejected(int tokens)
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 1_000);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => limiter.ReserveAsync(tokens, CancellationToken.None));
        await limiter.ReserveAsync(100, CancellationToken.None);
    }

    [Fact]
    public async Task FullQueueFailsExplicitly()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 0);
        await limiter.ReserveAsync(100, CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => limiter.ReserveAsync(1, CancellationToken.None));
        Assert.Contains("could not reserve", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentCompletionsRefundExactlyOnce()
    {
        using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 100);
        TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(100, CancellationToken.None);
        Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(100, CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?>[] completions = Enumerable.Range(0, 8).Select(async Task<Exception?> (_) =>
        {
            await start.Task;
            return Record.Exception(() => reservation.Complete(0));
        }).ToArray();

        start.SetResult();
        Exception?[] results = await Task.WhenAll(completions).WaitAsync(TestTimeout);
        Assert.Single(results, error => error is null);
        Assert.Equal(7, results.Count(error => error is InvalidOperationException));
        await waiting.WaitAsync(TestTimeout);

        using var cancellation = new CancellationTokenSource();
        Task blocked = limiter.ReserveAsync(1, cancellation.Token);
        Assert.False(blocked.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task ConcurrentCancellationAndRefundDoNotLeakCapacity()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            using var limiter = new TokenRateLimiter(tokenLimit: 100, queueLimit: 100);
            TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(100, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            Task<TokenRateLimiter.Reservation> waiting = limiter.ReserveAsync(100, cancellation.Token);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task cancel = Task.Run(async () =>
            {
                await start.Task;
                cancellation.Cancel();
            });
            Task refund = Task.Run(async () =>
            {
                await start.Task;
                reservation.Complete(0);
            });

            start.SetResult();
            await Task.WhenAll(cancel, refund).WaitAsync(TestTimeout);

            try
            {
                TokenRateLimiter.Reservation granted = await waiting.WaitAsync(TestTimeout);
                granted.Complete(0);
            }
            catch (OperationCanceledException)
            {
                Assert.True(waiting.IsCanceled);
            }

            await limiter.ReserveAsync(100, CancellationToken.None).WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task DisposeFailsAllWaitersAndRejectsNewReservations()
    {
        var clock = new ManualTimeProvider();
        var limiter = new TokenRateLimiter(100, 100, Window, clock);
        TokenRateLimiter.Reservation reservation = await limiter.ReserveAsync(100, CancellationToken.None);
        Task first = limiter.ReserveAsync(60, CancellationToken.None);
        Task second = limiter.ReserveAsync(40, CancellationToken.None);
        limiter.Dispose();
        limiter.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => limiter.ReserveAsync(1, CancellationToken.None));
        Assert.True(clock.TimerDisposed);
        clock.Advance(Window);
        clock.FireTimerCallback();
        reservation.Complete(0);
        Assert.Throws<InvalidOperationException>(() => reservation.Complete(0));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(-1, 0, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, -1)]
    public void InvalidConfigurationIsRejected(int tokenLimit, int queueLimit, int windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenRateLimiter(tokenLimit, queueLimit, TimeSpan.FromSeconds(windowSeconds)));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private ManualTimer? _timer;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public bool TimerDisposed => _timer?.Disposed == true;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Null(_timer);
            _timer = new ManualTimer(this, callback, state);
            _timer.Change(dueTime, period);
            return _timer;
        }

        public void Advance(TimeSpan elapsed, bool fireTimer = true)
        {
            Interlocked.Add(ref _timestamp, elapsed.Ticks);

            if (fireTimer)
            {
                _timer?.FireIfDue();
            }
        }

        public void FireTimerCallback() => _timer?.FireCallback();

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private readonly Lock _gate = new();
            private long _dueAt = long.MaxValue;

            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);

                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(Disposed, this);
                    _dueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                    return true;
                }
            }

            public void FireIfDue()
            {
                lock (_gate)
                {
                    if (Disposed || clock.GetTimestamp() < _dueAt)
                    {
                        return;
                    }

                    _dueAt = long.MaxValue;
                }

                FireCallback();
            }

            public void FireCallback() => callback(state);

            public void Dispose()
            {
                lock (_gate)
                {
                    Disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
