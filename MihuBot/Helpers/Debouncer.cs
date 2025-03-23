namespace MihuBot.Helpers;

#nullable enable

public sealed class Debouncer<T> : IDisposable where T : IEquatable<T>
{
    private readonly TimeSpan _delay;
    private readonly Func<T, CancellationToken, Task> _action;
    private readonly CancellationTokenSource _cts = new();

    private object? _lastValue;
    private bool _running;
    private bool _timerScheduled;
    private Timer? _timer;
    private DateTime _lastRun = DateTime.MinValue;

    private object Lock => _cts;

    public Debouncer(TimeSpan delay, Func<T, CancellationToken, Task> action)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(delay.TotalSeconds);
        ArgumentNullException.ThrowIfNull(action);

        _delay = delay;
        _action = action;
    }

    public void Update(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (Lock)
        {
            if (_lastValue is not null && EqualityComparer<T>.Default.Equals(value, (T)_lastValue))
            {
                return;
            }

            _lastValue = value;

            if (_running || _timerScheduled)
            {
                return;
            }

            ScheduleOrRun();
        }
    }

    private void ScheduleOrRun()
    {
        Debug.Assert(Monitor.IsEntered(Lock));

        TimeSpan sinceLastRun = DateTime.UtcNow - _lastRun;

        if (sinceLastRun < _delay)
        {
            _timerScheduled = true;
            _timer ??= new Timer(static s => ((Debouncer<T>)s!).RunAction(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_delay - sinceLastRun, Timeout.InfiniteTimeSpan);
        }
        else
        {
            RunAction();
        }
    }

    private void RunAction()
    {
        object? value;
        lock (Lock)
        {
            _running = true;
            _timerScheduled = false;

            value = _lastValue;
            _lastValue = null;
            _lastRun = DateTime.UtcNow;
        }

        Debug.Assert(value is not null);

        _ = Task.Run(() => RunActionAsyncCore((T)value), CancellationToken.None);
    }

    private async Task RunActionAsyncCore(T value)
    {
        try
        {
            await _action(value, _cts.Token);
        }
        catch { }

        lock (Lock)
        {
            _running = false;

            if (_lastValue is not null && !EqualityComparer<T>.Default.Equals(value, (T)_lastValue))
            {
                ScheduleOrRun();
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Cancel();
    }
}
