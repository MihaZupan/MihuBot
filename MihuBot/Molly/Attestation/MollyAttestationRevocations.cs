using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MihuBot.Molly.Attestation;

public enum MollyRevocationStatus
{
    /// <summary>No valid list has ever been loaded. Nothing can be verified.</summary>
    Unavailable,

    /// <summary>The list is older than <see cref="MollyAttestationRevocations.MaxAge"/>. Only already enrolled strict devices may log in.</summary>
    Stale,

    Fresh,
}

/// <summary>A validated copy of the official Android attestation revocation list.</summary>
public sealed class MollyRevocationSnapshot
{
    public MollyRevocationSnapshot(DateTimeOffset fetchedAt, IEnumerable<string> revokedSerials)
    {
        FetchedAt = fetchedAt;
        RevokedSerials = revokedSerials.ToFrozenSet(StringComparer.Ordinal);
    }

    public DateTimeOffset FetchedAt { get; }

    public FrozenSet<string> RevokedSerials { get; }

    /// <summary>
    /// Google documents the list keys as lowercase hex serials, but more than half of the live entries
    /// are evidently 64-bit serials written in decimal. Matching either form costs nothing, and missing
    /// a revoked (often leaked) key would be far worse than a theoretical cross-form collision.
    /// </summary>
    /// <param name="hexSerial">Lowercase hex without leading zeros, see <see cref="MollyAttestationCertificate.RevocationSerial"/>.</param>
    public bool IsRevoked(string hexSerial) =>
        RevokedSerials.Contains(hexSerial) ||
        (BigInteger.TryParse("0" + hexSerial, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out BigInteger value) &&
            RevokedSerials.Contains(value.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>
/// Caches the official Android key attestation revocation list
/// (https://developer.android.com/privacy-and-security/security-key-attestation#certificate_status),
/// persisting it with its real fetch time so a restart doesn't make stale data look fresh, nor lose it.
/// </summary>
public sealed class MollyAttestationRevocations
{
    public const string ListUrl = "https://android.googleapis.com/attestation/status";

    /// <summary>Beyond this age, enrollment and fallback logins are refused.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>The list is a few hundred KB today. Anything far larger is a mistake or an attack.</summary>
    private const int MaxListLength = 16 * 1024 * 1024;

    /// <summary>Covers the whole download, as the list is read in full by a single buffered request.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromMinutes(1);

    private const int MaxSerialLength = 128;

    private static readonly SearchValues<char> s_hexChars = SearchValues.Create("0123456789abcdefABCDEF");

    private readonly SynchronizedLocalJsonStore<CacheFile> _cache;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger<MollyAttestationRevocations> _logger;
    private MollyRevocationSnapshot? _snapshot;

    public MollyAttestationRevocations(ILogger<MollyAttestationRevocations> logger)
        : this("MollyAttestationRevocations.json", new HttpClient(), TimeProvider.System, logger)
    { }

    /// <param name="cachePath">Relative to <see cref="Constants.StateDirectory"/> unless rooted.</param>
    internal MollyAttestationRevocations(string cachePath, HttpClient http, TimeProvider time, ILogger<MollyAttestationRevocations> logger)
    {
        _http = http;
        _http.MaxResponseContentBufferSize = MaxListLength;
        _http.Timeout = RefreshTimeout;
        _time = time;
        _logger = logger;
        _cache = new SynchronizedLocalJsonStore<CacheFile>(cachePath);
        _snapshot = _cache.Query(cache => cache.Serials is null ? null : new MollyRevocationSnapshot(cache.FetchedAt, cache.Serials));
    }

    public MollyRevocationSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public MollyRevocationStatus GetStatus(out MollyRevocationSnapshot? snapshot)
    {
        snapshot = Snapshot;

        if (snapshot is null)
        {
            return MollyRevocationStatus.Unavailable;
        }

        TimeSpan age = _time.GetUtcNow() - snapshot.FetchedAt;

        // A fetch time in the future means the clock went backwards or the cache is bogus. Don't trust it as fresh.
        return age < TimeSpan.Zero || age > MaxAge ? MollyRevocationStatus.Stale : MollyRevocationStatus.Fresh;
    }

    /// <summary>
    /// Downloads, validates and persists the current list. A failure keeps the previous snapshot.
    /// Only called by <see cref="MollyAttestationRevocationRefresher"/>, one refresh at a time.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The client's MaxResponseContentBufferSize and Timeout bound the whole download.
            byte[] list = await _http.GetByteArrayAsync(ListUrl, cancellationToken);

            return Apply(list, _time.GetUtcNow());
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Anything from the network to a bad payload. The previous snapshot stays in effect.
            _logger.LogWarning(ex, "Failed to refresh the Android attestation revocation list");
            return false;
        }
    }

    /// <summary>
    /// Validates a downloaded list and makes it current. An invalid list changes nothing.
    /// </summary>
    /// <remarks>
    /// Serials are never forgotten: Google drops expired certificates from the list, but strict logins
    /// deliberately keep trusting expired enrollment chains, and a truncated or empty response mustn't
    /// lift revocations either. Lifted suspensions therefore stay in effect.
    /// </remarks>
    internal bool Apply(ReadOnlySpan<byte> json, DateTimeOffset fetchedAt)
    {
        if (!TryParseList(json, out string[]? serials, out int skipped))
        {
            _logger.LogWarning("Rejected a malformed Android attestation revocation list");
            return false;
        }

        if (skipped > 0)
        {
            _logger.LogWarning("Skipped {Count} unrecognized serials in the Android attestation revocation list", skipped);
        }

        IEnumerable<string> known = Snapshot?.RevokedSerials ?? [];
        var snapshot = new MollyRevocationSnapshot(fetchedAt, known.Concat(serials));
        Volatile.Write(ref _snapshot, snapshot);

        SaveCache(snapshot);

        return true;
    }

    /// <summary>
    /// The official format: <c>{"entries":{"&lt;serial hex&gt;":{"status":"REVOKED"|"SUSPENDED",...},...}}</c>.
    /// Every listed serial is treated as unusable, whatever its status or reason.
    /// </summary>
    /// <remarks>
    /// Only a malformed structure rejects the list. A single unexpected key is skipped instead, as
    /// rejecting the whole refresh would eventually make enrollment unavailable over one bad entry.
    /// </remarks>
    private static bool TryParseList(ReadOnlySpan<byte> json, [NotNullWhen(true)] out string[]? serials, out int skipped)
    {
        serials = null;
        skipped = 0;

        StatusList? list;

        try
        {
            list = JsonSerializer.Deserialize<StatusList>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (list?.Entries is null)
        {
            return false;
        }

        var normalized = new List<string>(list.Entries.Count);

        // The entry isn't inspected: being listed at all means the certificate can't be trusted.
        foreach (string key in list.Entries.Keys)
        {
            if (NormalizeSerial(key) is { } serial)
            {
                normalized.Add(serial);
            }
            else
            {
                skipped++;
            }
        }

        serials = [.. normalized];
        return true;
    }

    private sealed class StatusList
    {
        [JsonPropertyName("entries")]
        public Dictionary<string, StatusEntry>? Entries { get; set; }
    }

    private sealed class StatusEntry
    {
        /// <summary><c>REVOKED</c> or <c>SUSPENDED</c>.</summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    /// <summary>Lowercase hex without leading zeros, matching Java's <c>BigInteger.toString(16)</c>.</summary>
    internal static string? NormalizeSerial(string value)
    {
        if (value.Length is 0 or > MaxSerialLength || value.AsSpan().ContainsAnyExcept(s_hexChars))
        {
            return null;
        }

        string trimmed = value.ToLowerInvariant().TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private void SaveCache(MollyRevocationSnapshot snapshot)
    {
        _cache.Modify(cache =>
        {
            cache.FetchedAt = snapshot.FetchedAt;
            cache.Serials = [.. snapshot.RevokedSerials.Order(StringComparer.Ordinal)];
        });
    }

    private sealed class CacheFile
    {
        public DateTimeOffset FetchedAt { get; set; }

        public string[]? Serials { get; set; }
    }
}

/// <summary>Keeps <see cref="MollyAttestationRevocations"/> current: hourly, or every few minutes while refreshes fail.</summary>
public sealed class MollyAttestationRevocationRefresher : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    private readonly MollyAttestationRevocations _revocations;

    public MollyAttestationRevocationRefresher(MollyAttestationRevocations revocations)
    {
        _revocations = revocations;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);

        do
        {
            bool refreshed = await _revocations.RefreshAsync(stoppingToken);

            timer.Period = refreshed ? RefreshInterval : RetryInterval;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
