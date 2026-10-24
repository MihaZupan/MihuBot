namespace MihuBot.Helpers.RateLimiting;

public sealed class SimpleRateLimiter
{
    private readonly object _lock = new();
    private readonly TimeSpan _cooldown;
    private readonly int _maxTolerance;
    private readonly TimeProvider _timeProvider;
    private long _startTimestamp;
    private long _available;

    public SimpleRateLimiter(TimeSpan cooldown, int maxTolerance)
        : this(cooldown, maxTolerance, TimeProvider.System)
    {
    }

    internal SimpleRateLimiter(TimeSpan cooldown, int maxTolerance, TimeProvider timeProvider)
    {
        _cooldown = cooldown;
        _maxTolerance = maxTolerance;
        _available = maxTolerance;
        _timeProvider = timeProvider;
        _startTimestamp = timeProvider.GetTimestamp();
    }

    public bool TryEnter(int count = 1)
    {
        lock (_lock)
        {
            long timestamp = _timeProvider.GetTimestamp();
            TimeSpan elapsed = _timeProvider.GetElapsedTime(_startTimestamp, timestamp);
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
