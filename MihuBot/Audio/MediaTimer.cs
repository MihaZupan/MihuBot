using System.Threading.Channels;

#nullable enable

namespace MihuBot.Audio;

internal sealed class MediaTimer<TFrameData> : IAsyncDisposable
{
    private const double MaxTimeDeltaMsBeforeReset = 1_000;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<TFrameData> _frameChannel;
    private readonly Task _loopTask;

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

    private CancellationTokenRegistration RegisterCancellation(CancellationToken cancellationToken) =>
        cancellationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _cts);

    public MediaTimer(double framerate, TimeSpan bufferSize, TimeSpan prolongedSilence)
    {
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
        using var _ = RegisterCancellation(cancellationToken);

        await _frameChannel.Writer.WriteAsync(frame, _cts.Token);
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

                    await OnFrameAsync(frame);
                }
                else
                {
                    if (++_silenceFrames <= _maxSilenceFrames)
                    {
                        await OnSilenceFrameAsync();
                    }
                    else
                    {
                        await OnProlongedSilenceAsync();

                        await _frameChannel.Reader.WaitToReadAsync(_cts.Token);

                        ResetAfterSilence();
                    }
                }
            }
        }
        catch when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public async Task ClearAsync()
    {
        while (_frameChannel.Reader.TryRead(out TFrameData? frame))
        {
            await OnClearedFrameAsync(frame);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        await _loopTask;
    }
}