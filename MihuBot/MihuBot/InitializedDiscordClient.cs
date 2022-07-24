namespace MihuBot;

public sealed class InitializedDiscordClient : DiscordSocketClient
{
    private readonly TokenType _tokenType;
    private readonly string _token;

    private Task _initializerTcsTask;
    private TaskCompletionSource _initializedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InitializedDiscordClient(DiscordSocketConfig config, TokenType tokenType, string token)
        : base(config)
    {
        _tokenType = tokenType;
        _token = token;
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initializerTcsTask is null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _initializerTcsTask, tcs.Task, null) is null)
            {
                try
                {
                    await InitializeAsync();
                    tcs.SetResult();
                    _initializedTcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                    _initializedTcs.SetException(ex);
                }
            }
        }

        await _initializerTcsTask;
    }

    public Task WaitUntilInitializedAsync() => _initializedTcs.Task;

    private async Task InitializeAsync()
    {
        Log += e =>
        {
            if (e.Exception is not null && !_initializedTcs.Task.IsCompleted)
            {
                Console.WriteLine($"Init error: {e.Message} - {e.Exception}");
            }

            return Task.CompletedTask;
        };

        var onConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Connected += () => { onConnectedTcs.TrySetResult(); return Task.CompletedTask; };

        var onReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ready += () => { onReadyTcs.TrySetResult(); return Task.CompletedTask; };

        await LoginAsync(_tokenType, _token).WaitAsync(TimeSpan.FromSeconds(15));
        await StartAsync().WaitAsync(TimeSpan.FromSeconds(15));

        await onConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await onReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
