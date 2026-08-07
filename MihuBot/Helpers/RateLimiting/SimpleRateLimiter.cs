namespace MihuBot.Helpers.RateLimiting;

public sealed class SimpleRateLimiter
{
    private readonly object _lock = new();
    private readonly TimeSpan _cooldown;
    private readonly int _maxTolerance;
    private long _startTimestamp = Stopwatch.GetTimestamp();
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
            long timestamp = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_startTimestamp, timestamp);
            long max = (long)(elapsed / _cooldown) + _available;

            if (max > _maxTolerance)
            {
                _available = _maxTolerance - count;
                _startTimestamp = timestamp;
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
