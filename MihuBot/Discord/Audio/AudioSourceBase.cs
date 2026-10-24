namespace MihuBot.Discord.Audio;

public abstract class AudioSourceBase : IAudioSource
{
    public SocketGuildUser Requester { get; }

    public bool IsAutoplay { get; }

    private readonly Lock _lifecycleLock = new();
    private readonly TimeSpan _initializationTimeout;
    private bool _startedInitializing;
    private Task _initializationTask = Task.CompletedTask;
    private Task _disposeTask;
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposedCts = new();

    public AudioSourceBase(SocketGuildUser requester, bool isAutoplay = false)
        : this(requester, isAutoplay, TimeSpan.FromSeconds(30))
    {
    }

    protected AudioSourceBase(SocketGuildUser requester, bool isAutoplay, TimeSpan initializationTimeout)
    {
        Requester = requester;
        IsAutoplay = isAutoplay;
        _initializationTimeout = initializationTimeout;
    }

    protected CancellationToken LifetimeCancellationToken => _disposedCts.Token;

    public void StartInitializing(int bitrateHintKbit)
    {
        lock (_lifecycleLock)
        {
            if (_startedInitializing || _disposeTask is not null)
            {
                return;
            }

            _startedInitializing = true;
            _initializationTask = Task.Run(() => InitializeCoreAsync(bitrateHintKbit));
        }
    }

    private async Task InitializeCoreAsync(int bitrateHintKbit)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposedCts.Token);
        timeout.CancelAfter(_initializationTimeout);

        try
        {
            await InitializeAsync(bitrateHintKbit, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();

            lock (_lifecycleLock)
            {
                if (_disposeTask is null)
                {
                    _initialized.TrySetResult(true);
                }
                else
                {
                    _initialized.TrySetCanceled(_disposedCts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested)
        {
            _initialized.TrySetCanceled(_disposedCts.Token);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            _initialized.TrySetException(new TimeoutException($"Audio initialization timed out for '{Description}'.", ex));
        }
        catch (Exception ex)
        {
            _initialized.TrySetException(ex);
        }
        finally
        {
            if (!_initialized.Task.IsCompletedSuccessfully)
            {
                // Disposal joins this task, so initialization must not await it.
                _ = DisposeAsync().AsTask();
            }
        }
    }

    public Task<bool> EnsureInitializedAsync() => _initialized.Task;

    protected abstract Task InitializeAsync(int bitrateHintKbit, CancellationToken cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                Task initialization = _initializationTask;
                _disposeTask = Task.Run(() => DisposeCoreAsync(initialization));
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task initialization)
    {
        var cleanup = new AsyncCleanup();
        cleanup.Run(_disposedCts.Cancel);
        _initialized.TrySetCanceled(_disposedCts.Token);
        await cleanup.RunAsync(() => initialization);

        if (_initialized.Task.IsFaulted)
        {
            _ = _initialized.Task.Exception;
        }

        await cleanup.RunAsync(() => DisposeResourcesAsync().AsTask());
        cleanup.Run(_disposedCts.Dispose);
        cleanup.ThrowIfFailed();
    }

    protected abstract ValueTask DisposeResourcesAsync();

    public abstract TimeSpan? Remaining { get; }

    public abstract TimeSpan? Duration { get; }

    public abstract string Description { get; }

    public virtual string Author => null;

    public abstract string Url { get; }

    public abstract string ThumbnailUrl { get; }

    public abstract ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);
}
