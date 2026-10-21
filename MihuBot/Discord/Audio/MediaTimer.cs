using System.Threading.Channels;
using System.Runtime.ExceptionServices;

#nullable enable

namespace MihuBot.Discord.Audio;

internal sealed class MediaTimer<TFrameData> : IAsyncDisposable
{
    private const double MaxTimeDeltaMsBeforeReset = 1_000;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<TFrameData> _frameChannel;
    private readonly Task _loopTask;
    private readonly Lock _disposeLock = new();
    private Task? _disposeTask;
    private readonly TimeSpan _callbackTimeout;
    private readonly Action<Exception> _onError;

    private readonly Stopwatch _stopwatch = new();
    private readonly double _averageWaitMs;
    private readonly int _maxSilenceFrames;
    private long _frameCount;
    private TimeSpan _lastFrameTime;
    private int _silenceFrames;

    private double MinWaitTimeMs => 0.25 * _averageWaitMs;

    public Func<TFrameData, Task> OnFrameAsync { get; set; } = _ => Task.CompletedTask;
    public Func<Task> OnSilenceFrameAsync { get; set; } = () => Task.CompletedTask;
    public Func<Task> OnProlongedSilenceAsync { get; set; } = () => Task.CompletedTask;
    public Func<TFrameData, Task> OnClearedFrameAsync { get; set; } = _ => Task.CompletedTask;

    public MediaTimer(double framerate, TimeSpan bufferSize, TimeSpan prolongedSilence,
        Action<Exception>? onError = null, TimeSpan? callbackTimeout = null)
    {
        _onError = onError ?? Console.WriteLine;
        _callbackTimeout = callbackTimeout ?? TimeSpan.FromSeconds(5);
        double averageWaitMs = 1000 / framerate;

        _averageWaitMs = averageWaitMs;
        _maxSilenceFrames = Math.Max(1, (int)(prolongedSilence.TotalMilliseconds / averageWaitMs));

        int framesToBuffer = Math.Max(1, (int)(bufferSize.TotalMilliseconds / averageWaitMs));

        _frameChannel = Channel.CreateBounded<TFrameData>(new BoundedChannelOptions(framesToBuffer)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true
        });

        ResetAfterSilence();

        using (ExecutionContext.SuppressFlow())
        {
            _loopTask = Task.Run(LoopAsync, CancellationToken.None);
        }
    }

    public async Task EnqueueAsync(TFrameData frame, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            await _frameChannel.Writer.WriteAsync(frame, linked.Token);
        }
        catch (ChannelClosedException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private bool TryGetNextFrameWaitTime(out int waitMs)
    {
        TimeSpan elapsed = _stopwatch.Elapsed;
        TimeSpan nextFrameTime = TimeSpan.FromMilliseconds(_frameCount * _averageWaitMs);

        double msUntilNextFrame = (nextFrameTime - elapsed).TotalMilliseconds;
        double msSinceLastFrame = (elapsed - _lastFrameTime).TotalMilliseconds;

        waitMs = 0;

        if (Math.Abs(msUntilNextFrame) > MaxTimeDeltaMsBeforeReset)
        {
            // Time is too far off, reset
            ResetAfterSilence();
        }
        else if (msUntilNextFrame > 0)
        {
            // Wait until _averageWaitMs has passed
            waitMs = Math.Max(1, (int)(msUntilNextFrame / 2));
        }
        else if (msSinceLastFrame > 0 && msSinceLastFrame < MinWaitTimeMs)
        {
            // Not enough time has passed since last wait
            waitMs = 1;
        }

        if (waitMs > 0)
        {
            return true;
        }

        _lastFrameTime = elapsed;
        _frameCount++;
        return false;
    }

    private void ResetAfterSilence()
    {
        _stopwatch.Restart();
        _lastFrameTime = -TimeSpan.FromMilliseconds(_averageWaitMs);
        _frameCount = 0;
    }

    private async Task LoopAsync()
    {
        Exception? failure = null;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (TryGetNextFrameWaitTime(out int waitMs))
                {
                    await Task.Delay(waitMs, _cts.Token);
                    continue;
                }

                if (_frameChannel.Reader.TryRead(out TFrameData? frame))
                {
                    _silenceFrames = 0;

                    await RunCallbackAsync(OnFrameAsync(frame));
                }
                else
                {
                    if (++_silenceFrames <= _maxSilenceFrames)
                    {
                        await RunCallbackAsync(OnSilenceFrameAsync());
                    }
                    else
                    {
                        await RunCallbackAsync(OnProlongedSilenceAsync());

                        await _frameChannel.Reader.WaitToReadAsync(_cts.Token);

                        ResetAfterSilence();
                    }
                }
            }
        }
        catch when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            _frameChannel.Writer.TryComplete(ex);
            _onError(ex);
        }
        finally
        {
            _frameChannel.Writer.TryComplete(failure);
        }
    }

    private async Task RunCallbackAsync(Task callback)
    {
        try
        {
            await callback.WaitAsync(_callbackTimeout, _cts.Token);
        }
        catch
        {
            if (!callback.IsCompleted)
            {
                _ = ObserveCallbackAsync(callback);
            }

            throw;
        }
    }

    private async Task ObserveCallbackAsync(Task callback)
    {
        try
        {
            await callback;
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    public async Task ClearAsync()
    {
        var cleanup = new AsyncCleanup();

        while (_frameChannel.Reader.TryRead(out TFrameData? frame))
        {
            await cleanup.RunAsync(() => OnClearedFrameAsync(frame));
        }

        cleanup.ThrowIfFailed();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var cleanup = new AsyncCleanup();
        cleanup.Run(_cts.Cancel);
        _frameChannel.Writer.TryComplete();
        await cleanup.RunAsync(() => _loopTask);
        await cleanup.RunAsync(ClearAsync);
        cleanup.Run(_cts.Dispose);
        cleanup.ThrowIfFailed();
    }
}