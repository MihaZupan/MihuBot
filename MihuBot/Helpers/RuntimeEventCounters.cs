namespace MihuBot.Helpers;

public static class RuntimeEventCounters
{
    private static readonly string[] _systemRuntime = new[]
    {
        "cpu-usage",
        "working-set",
        "gc-heap-size",
        "gen-0-gc-count",
        "gen-1-gc-count",
        "gen-2-gc-count",
        "threadpool-thread-count",
        "monitor-lock-contention-count",
        "threadpool-queue-length",
        "threadpool-completed-items-count",
        "alloc-rate",
        "active-timer-count",
        "gc-fragmentation",
        "gc-committed",
        "exception-count",
        "time-in-gc",
        "gen-0-size",
        "gen-1-size",
        "gen-2-size",
        "loh-size",
        "poh-size",
        "assembly-count",
        "il-bytes-jitted",
        "methods-jitted-count",
        "time-in-jit",
    };

    private static readonly string[] _aspNetCoreHosting = new[]
    {
        "requests-per-second",
        "total-requests",
        "current-requests",
        "failed-requests",
    };

    private static readonly string[] _aspNetCoreHttpConnections = new[]
    {
        "connections-started",
        "connections-stopped",
        "connections-timed-out",
        "current-connections",
        "connections-duration",
    };

    private static readonly string[] _aspNetCoreKestrel = new[]
    {
        "connections-per-second",
        "total-connections",
        "tls-handshakes-per-second",
        "total-tls-handshakes",
        "current-tls-handshakes",
        "failed-tls-handshakes",
        "current-connections",
        "connection-queue-length",
        "request-queue-length",
        "current-upgraded-requests",
    };

    private static readonly string[] _systemNetHttp = new[]
    {
        "requests-started",
        "requests-started-rate",
        "requests-failed",
        "requests-failed-rate",
        "current-requests",
        "http11-connections-current-total",
        "http20-connections-current-total",
        "http30-connections-current-total",
        "http11-requests-queue-duration",
        "http20-requests-queue-duration",
        "http30-requests-queue-duration",
    };

    private static readonly string[] _systemNetNameResolution = new[]
    {
        "dns-lookups-requested",
        "current-dns-lookups",
        "dns-lookups-duration",
    };

    private static readonly string[] _systemNetSecurity = new[]
    {
        "tls-handshake-rate",
        "total-tls-handshakes",
        "current-tls-handshakes",
        "failed-tls-handshakes",
        "all-tls-sessions-open",
        "tls10-sessions-open",
        "tls11-sessions-open",
        "tls12-sessions-open",
        "tls13-sessions-open",
        "all-tls-handshake-duration",
        "tls10-handshake-duration",
        "tls11-handshake-duration",
        "tls12-handshake-duration",
        "tls13-handshake-duration",
    };

    private static readonly string[] _systemNetSockets = new[]
    {
        "current-outgoing-connect-attempts",
        "outgoing-connections-established",
        "incoming-connections-established",
        "bytes-received",
        "bytes-sent",
        "datagrams-received",
        "datagrams-sent",
    };

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> EventCounters { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        { "System.Runtime",                         _systemRuntime.AsReadOnly() },
        { "Microsoft.AspNetCore.Hosting",           _aspNetCoreHosting.AsReadOnly() },
        { "Microsoft.AspNetCore.Http.Connections",  _aspNetCoreHttpConnections.AsReadOnly() },
        { "Microsoft-AspNetCore-Server-Kestrel",    _aspNetCoreKestrel.AsReadOnly() },
        { "System.Net.Http",                        _systemNetHttp.AsReadOnly() },
        { "System.Net.NameResolution",              _systemNetNameResolution.AsReadOnly() },
        { "System.Net.Security",                    _systemNetSecurity.AsReadOnly() },
        { "System.Net.Sockets",                     _systemNetSockets.AsReadOnly() },
    }.AsReadOnly();
}
