using System.Collections.Concurrent;
using MihuBot.Discord;

namespace MihuBot.Tests.Discord;

public sealed class RunningDiscordCommandsTests
{
    [Fact]
    public async Task SynchronouslyCompletedDispatchIsNeverRegistered()
    {
        var runs = new RunningDiscordCommands(CancellationToken.None, ex => Assert.Fail(ex.ToString()));

        await runs.RunAsync(1, token =>
        {
            Assert.True(runs.CancelAsync(1).IsCompletedSuccessfully);
            Assert.False(token.IsCancellationRequested);
            return Task.CompletedTask;
        });

        await runs.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousDispatchFailureDoesNotLeaveRegisteredWork(bool throws)
    {
        var runs = new RunningDiscordCommands(CancellationToken.None, ex => Assert.Fail(ex.ToString()));
        var failure = new InvalidOperationException("Expected dispatch failure.");

        Exception? actual = await Record.ExceptionAsync(() => runs.RunAsync(1, _ =>
            throws ? throw failure : Task.FromException(failure)));

        Assert.Same(failure, actual);
        await runs.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AsynchronousDispatchTracksAllHandlersUntilTheyComplete()
    {
        var runs = new RunningDiscordCommands(CancellationToken.None, ex => Assert.Fail(ex.ToString()));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        Task dispatch = runs.RunAsync(1, token =>
        {
            observedToken = token;
            return Task.WhenAll(first.Task, second.Task);
        });

        await runs.CancelAsync(1);
        Assert.True(observedToken.IsCancellationRequested);

        Task stop = runs.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);
        first.SetResult();
        Assert.False(stop.IsCompleted);
        second.SetResult();

        await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DispatchIsNotInvokedAfterShutdownBegins()
    {
        using var stopping = new CancellationTokenSource();
        var runs = new RunningDiscordCommands(stopping.Token, ex => Assert.Fail(ex.ToString()));
        await stopping.CancelAsync();

        await runs.RunAsync(1, _ => throw new InvalidOperationException("Dispatch must not run."));
        await runs.StopAsync(CancellationToken.None);
        await runs.RunAsync(2, _ => throw new InvalidOperationException("Dispatch must not run."));
    }

    [Fact]
    public async Task ApplicationStoppingNotifiesEveryRunBeforeStopAsync()
    {
        using var stopping = new CancellationTokenSource();
        var runs = new RunningDiscordCommands(stopping.Token, ex => Assert.Fail(ex.ToString()));
        using var first = runs.TryStart(1);
        using var second = runs.TryStart(1);
        using var third = runs.TryStart(2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        await stopping.CancelAsync();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.True(third.Token.IsCancellationRequested);
        Assert.Null(runs.TryStart(3));
    }

    [Fact]
    public async Task StopNotifiesQueuedWorkAndWaitsForEveryRunToFinish()
    {
        var runs = new RunningDiscordCommands(CancellationToken.None, ex => Assert.Fail(ex.ToString()));
        using var first = runs.TryStart(1);
        using var second = runs.TryStart(1);
        Assert.NotNull(first);
        Assert.NotNull(second);

        Task stop = runs.StopAsync(CancellationToken.None);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        Assert.Null(runs.TryStart(2));

        first.Dispose();
        Assert.False(stop.IsCompleted);

        second.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await runs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReactionCancelsAllHandlersButDoesNotForgetTheirCleanup()
    {
        var runs = new RunningDiscordCommands(CancellationToken.None, ex => Assert.Fail(ex.ToString()));
        using var completed = runs.TryStart(1);
        using var first = runs.TryStart(1);
        using var second = runs.TryStart(1);
        using var unrelated = runs.TryStart(2);
        Assert.NotNull(completed);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(unrelated);
        completed.Dispose();

        await runs.CancelAsync(1);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(unrelated.Token.IsCancellationRequested);

        Task stop = runs.StopAsync(CancellationToken.None);
        unrelated.Dispose();
        first.Dispose();
        Assert.False(stop.IsCompleted);

        second.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotPreventOtherNotificationsOrCleanup()
    {
        var errors = new ConcurrentQueue<Exception>();
        var errorLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new RunningDiscordCommands(CancellationToken.None, ex =>
        {
            errors.Enqueue(ex);
            errorLogged.SetResult();
        });
        using var first = runs.TryStart(1);
        using var second = runs.TryStart(2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        using var registration = first.Token.Register(() => throw new InvalidOperationException("Expected callback failure."));

        Task stop = runs.StopAsync(CancellationToken.None);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);

        await errorLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.Dispose();
        second.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<AggregateException>(Assert.Single(errors));
    }

    [Fact]
    public async Task ExpiredShutdownDeadlineStillNotifiesAllRuns()
    {
        var runs = new RunningDiscordCommands(CancellationToken.None, ex => Assert.Fail(ex.ToString()));
        using var first = runs.TryStart(1);
        using var second = runs.TryStart(2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runs.StopAsync(deadline.Token));

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Null(runs.TryStart(3));
    }

    [Fact]
    public async Task ConcurrentCompletionAndCancellationAreSafe()
    {
        var errors = new ConcurrentQueue<Exception>();
        var runs = new RunningDiscordCommands(CancellationToken.None, errors.Enqueue);

        for (int i = 0; i < 100; i++)
        {
            using var run = runs.TryStart(1);
            Assert.NotNull(run);
            await Task.WhenAll(Task.Run(run.Dispose), Task.Run(() => runs.CancelAsync(1)));
        }

        await runs.StopAsync(CancellationToken.None);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ShutdownWaitsForDispatchThatHasNotRegisteredYet()
    {
        var runs = new RunningDiscordCommands(CancellationToken.None, ex => Assert.Fail(ex.ToString()));
        using var entered = new ManualResetEventSlim();
        using var continueDispatch = new ManualResetEventSlim();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;

        Task dispatch = Task.Run(() => runs.RunAsync(1, token =>
        {
            observedToken = token;
            entered.Set();
            Assert.True(continueDispatch.Wait(TimeSpan.FromSeconds(10)));
            return cleanup.Task;
        }));

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Task stop = runs.StopAsync(CancellationToken.None);
            Assert.False(stop.IsCompleted);
            Assert.Null(runs.TryStart(2));

            continueDispatch.Set();
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = observedToken.Register(() => notified.TrySetResult());
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stop.IsCompleted);

            cleanup.SetResult();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            continueDispatch.Set();
            cleanup.TrySetResult();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ConcurrentAddsAndRemovalsPreserveOtherRunsForTheSameMessage()
    {
        var errors = new ConcurrentQueue<Exception>();
        var runs = new RunningDiscordCommands(CancellationToken.None, errors.Enqueue);
        using var retained = runs.TryStart(1);
        Assert.NotNull(retained);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            using var run = runs.TryStart(1);
            Assert.NotNull(run);
            await runs.CancelAsync(1);
        })));

        Task stop = runs.StopAsync(CancellationToken.None);
        Assert.True(retained.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        retained.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(errors);
    }
}
