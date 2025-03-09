namespace MihuBot.Audio;

public abstract class AudioSourceBase : IAudioSource
{
    public SocketGuildUser Requester { get; }

    private int _startedInitializing;
    private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposedCts = new();

    public AudioSourceBase(SocketGuildUser requester)
    {
        Requester = requester;
    }

    public void StartInitializing(int bitrateHintKbit)
    {
        Task.Run(async () =>
        {
            if (Interlocked.Exchange(ref _startedInitializing, 1) != 0)
            {
                return;
            }

            try
            {
                await InitializeAsync(bitrateHintKbit, _disposedCts.Token);
                _initialized.SetResult(true);
            }
            catch
            {
                _initialized.SetResult(false);
                await DisposeAsync();
            }
        });
    }

    public Task<bool> EnsureInitializedAsync() => _initialized.Task;

    public abstract Task InitializeAsync(int bitrateHintKbit, CancellationToken cancellationToken);

    public virtual ValueTask DisposeAsync()
    {
        _disposedCts.Cancel();
        return default;
    }


    public abstract TimeSpan? Remaining { get; }

    public abstract string Description { get; }

    public abstract string Url { get; }

    public abstract string ThumbnailUrl { get; }

    public abstract ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);
}
