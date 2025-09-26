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
    private CancellationTokenSource? _currentActionCts;

    private object Lock => _cts;

    public bool CancelPendingActions { get; set; }

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

            if (CancelPendingActions)
            {
                _currentActionCts?.Cancel();
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
        CancellationToken actionCt;

        object? value;
        lock (Lock)
        {
            _running = true;
            _timerScheduled = false;

            value = _lastValue;
            _lastRun = DateTime.UtcNow;

            _currentActionCts = new CancellationTokenSource();
            actionCt = _currentActionCts.Token;
        }

        Debug.Assert(value is not null);

        _ = Task.Run(() => RunActionAsyncCore((T)value, actionCt), CancellationToken.None);
    }

    private async Task RunActionAsyncCore(T value, CancellationToken actionCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, actionCt);

        try
        {
            await _action(value, cts.Token);
        }
        catch { }

        lock (Lock)
        {
            _running = false;
            _currentActionCts = null;

            if (_lastValue is not null && !EqualityComparer<T>.Default.Equals(value, (T)_lastValue))
            {
                ScheduleOrRun();
            }
            else
            {
                _lastValue = null;
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Cancel();
    }
}
