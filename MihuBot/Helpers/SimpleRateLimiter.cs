namespace MihuBot.Helpers;

public sealed class SimpleRateLimiter
{
    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TimeSpan _cooldown;
    private readonly int _maxTolerance;
    private long _available;

    public SimpleRateLimiter(TimeSpan cooldown, int maxTolerance)
    {
        _cooldown = cooldown;
        _maxTolerance = maxTolerance;
        _available = maxTolerance;
    }

    public bool TryEnter(int count = 1)
    {
        lock (_lock)
        {
            TimeSpan elapsed = _stopwatch.Elapsed;
            long max = (long)(elapsed / _cooldown) + _available;

            if (max > _maxTolerance)
            {
                _available = _maxTolerance - count;
                _stopwatch.Restart();
                return true;
            }

            if (max >= count)
            {
                _available -= count;
                return true;
            }

            return false;
        }
    }
}
