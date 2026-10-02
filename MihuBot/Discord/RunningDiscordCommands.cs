using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MihuBot.Discord;

internal sealed class RunningDiscordCommands(CancellationToken applicationStopping, Action<Exception> logCancellationError)
{
    private readonly ConcurrentDictionary<ulong, ImmutableList<Run>> _runs = [];
    private readonly TaskCompletionSource _admissionsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admissions;
    private bool _stopping;

    public Run TryStart(ulong messageId)
    {
        if (!TryEnter())
        {
            return null;
        }

        try
        {
            var run = new Run(this, messageId, applicationStopping);
            Add(run);
            return run;
        }
        finally
        {
            Exit();
        }
    }

    public Task RunAsync(ulong messageId, Func<CancellationToken, Task> action)
    {
        Run run;
        Task task;

        if (!TryEnter())
        {
            return Task.CompletedTask;
        }

        try
        {
            run = new Run(this, messageId, applicationStopping);

            try
            {
                task = action(run.Token);

                if (!task.IsCompleted)
                {
                    Add(run);
                }
            }
            catch
            {
                run.Dispose();
                throw;
            }
        }
        finally
        {
            Exit();
        }

        return CompleteAsync(run, task);
    }

    private void Add(Run run)
    {
        _runs.AddOrUpdate(run.MessageId,
            static (_, run) => ImmutableList.Create(run),
            static (_, runs, run) => runs.Add(run), run);

        if (Volatile.Read(ref _stopping))
        {
            _ = run.CancelAsync();
        }
    }

    private bool TryEnter()
    {
        Interlocked.Increment(ref _admissions);

        if (Volatile.Read(ref _stopping) || applicationStopping.IsCancellationRequested)
        {
            Exit();
            return false;
        }

        return true;
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _admissions) == 0 && Volatile.Read(ref _stopping))
        {
            _admissionsDrained.TrySetResult();
        }
    }

    private static async Task CompleteAsync(Run run, Task task)
    {
        using (run)
        {
            await task;
        }
    }

    public Task CancelAsync(ulong messageId)
    {
        return _runs.TryGetValue(messageId, out var runs)
            ? Task.WhenAll(runs.Select(run => run.CancelAsync()))
            : Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, true);

        if (Volatile.Read(ref _admissions) == 0)
        {
            _admissionsDrained.TrySetResult();
        }

        // Notify every run before waiting for any callbacks or command cleanup.
        Task cancellation = Task.WhenAll(_runs.Values.SelectMany(runs => runs).Select(run => run.CancelAsync()));

        // Dispatch may still be running synchronously before registering unfinished work.
        await _admissionsDrained.Task.WaitAsync(cancellationToken);
        Run[] runs = _runs.Values.SelectMany(messageRuns => messageRuns).ToArray();
        await cancellation.WaitAsync(cancellationToken);
        await Task.WhenAll(runs.Select(run => run.CancelAsync())).WaitAsync(cancellationToken);
        await Task.WhenAll(runs.Select(run => run.Completion.Task)).WaitAsync(cancellationToken);
    }

    internal sealed class Run : IDisposable
    {
        private readonly RunningDiscordCommands _owner;
        private readonly Lock _lock = new();
        private readonly CancellationTokenSource _cancellation;
        private bool _disposed;

        public ulong MessageId { get; }
        public CancellationToken Token { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Run(RunningDiscordCommands owner, ulong messageId, CancellationToken applicationStopping)
        {
            _owner = owner;
            MessageId = messageId;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            Token = _cancellation.Token;
        }

        public async Task CancelAsync()
        {
            try
            {
                Task cancellation;

                lock (_lock)
                {
                    cancellation = _disposed ? Task.CompletedTask : _cancellation.CancelAsync();
                }

                await cancellation;
            }
            catch (Exception ex)
            {
                _owner.LogCancellationError(ex);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _cancellation.Dispose();
            }

            while (_owner._runs.TryGetValue(MessageId, out var messageRuns))
            {
                var remaining = messageRuns.Remove(this);
                bool removed = remaining.IsEmpty
                    ? _owner._runs.TryRemove(new KeyValuePair<ulong, ImmutableList<Run>>(MessageId, messageRuns))
                    : _owner._runs.TryUpdate(MessageId, remaining, messageRuns);

                if (removed)
                {
                    break;
                }
            }

            Completion.TrySetResult();
        }
    }

    private void LogCancellationError(Exception exception) => logCancellationError(exception);
}
