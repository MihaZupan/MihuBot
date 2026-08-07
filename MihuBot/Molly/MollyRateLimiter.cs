using System.Collections.Concurrent;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// Per-IP rate limiter for the Molly APIs. Each client gets its own <see cref="SimpleRateLimiter"/>
/// token bucket: a burst allowance to cover normal usage, refilling at one request per cooldown.
/// </summary>
/// <remarks>
/// Requests are also gated on being decryptable with the app secret, so this only has to keep a
/// misbehaving (or compromised) real client in check rather than block arbitrary internet traffic.
/// </remarks>
public sealed class MollyRateLimiter
{
    /// <summary>How many requests a client may make back-to-back before being throttled.</summary>
    private const int DefaultBurst = 10;

    /// <summary>How long it takes to earn one more request once the burst is used up.</summary>
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private const int MaxTrackedClients = 100_000;

    private readonly TimeSpan _cooldown;
    private readonly int _burst;
    private readonly ConcurrentDictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private long _lastCleanupTimestamp = Stopwatch.GetTimestamp();

    public MollyRateLimiter() : this(DefaultCooldown, DefaultBurst) { }

    public MollyRateLimiter(TimeSpan cooldown, int burst)
    {
        _cooldown = cooldown;
        _burst = burst;
    }

    /// <summary>
    /// Charges the request against the caller's X-Real-IP when the reverse proxy provides one,
    /// and against the connection address otherwise.
    /// </summary>
    /// <remarks>
    /// The connection address is deliberately not charged when X-Real-IP is present: behind a reverse
    /// proxy it identifies the shared proxy rather than the caller, so limiting on it would let one
    /// noisy device throttle everyone. The limit is therefore only as trustworthy as the proxy setting
    /// the header - a client connecting directly can spoof it, which the request encryption guards against.
    /// </remarks>
    public bool TryEnter(HttpContext context, out TimeSpan retryAfter)
    {
        CleanupIfNeeded();

        if (GetClientKey(context) is not { } key)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        Client client = _clients.GetOrAdd(key, CreateClient);
        Volatile.Write(ref client.LastUsedTimestamp, Stopwatch.GetTimestamp());

        bool allowed = client.Limiter.TryEnter();

        // The bucket doesn't expose when the next token lands, so report the upper bound.
        retryAfter = allowed ? TimeSpan.Zero : _cooldown;
        return allowed;
    }

    private static string? GetClientKey(HttpContext context)
    {
        // Only trust the header when it actually is an IP.
        if (context.Request.Headers["X-Real-IP"] is [{ Length: > 0 and <= 64 } realIpValue] &&
            IPAddress.TryParse(realIpValue, out IPAddress? realIp))
        {
            return $"r:{realIp.MapToIPv6()}";
        }

        return context.Connection.RemoteIpAddress is { } remoteIp
            ? $"c:{remoteIp.MapToIPv6()}"
            : null;
    }

    private Client CreateClient(string key) => new() { Limiter = new SimpleRateLimiter(_cooldown, _burst) };

    private void CleanupIfNeeded()
    {
        long now = Stopwatch.GetTimestamp();
        long lastCleanup = Volatile.Read(ref _lastCleanupTimestamp);

        if (Stopwatch.GetElapsedTime(lastCleanup, now) < CleanupInterval && _clients.Count < MaxTrackedClients)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupTimestamp, now, lastCleanup) != lastCleanup)
        {
            return;
        }

        // Once a bucket has had enough time to refill completely, dropping it is indistinguishable
        // from keeping it, so eviction can never be used to skip a cooldown.
        TimeSpan fullRefill = _cooldown * _burst;

        foreach ((string key, Client client) in _clients)
        {
            if (Stopwatch.GetElapsedTime(Volatile.Read(ref client.LastUsedTimestamp), now) > fullRefill)
            {
                _clients.TryRemove(new KeyValuePair<string, Client>(key, client));
            }
        }
    }

    private sealed class Client
    {
        public required SimpleRateLimiter Limiter { get; init; }

        public long LastUsedTimestamp = Stopwatch.GetTimestamp();
    }
}
