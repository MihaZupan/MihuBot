using System.Net.Http.Json;

namespace MihuBot.RuntimeUtils;

/// <summary>
/// Keeps up-to-date information about how long work items currently have to wait before they start running
/// on public Helix queues, so that jobs can pick a queue that is likely to become available sooner.
/// </summary>
/// <remarks>
/// Helix's 'info/queues' endpoint exposes a QueueDepth, but it is always reported as 0 and can't be used.
/// Instead we sample recent Helix jobs (from every Helix customer, not just our own) and look at the
/// Queued/Started timestamps of their work items. A work item that is still waiting tells us how backed up
/// the queue is right now, and one that already started tells us what delay the queue is handing out.
/// </remarks>
public sealed class HelixAvailabilityService : PeriodicBackgroundService
{
    private const string ApiVersion = "api-version=2019-06-17";
    private const string QueueInfoUrl = $"https://helix.dot.net/api/info/queues?{ApiVersion}";

    /// <summary>
    /// The job list can't be filtered by queue (Helix's 'properties' filter returns a 500), so we ask for enough
    /// jobs across all queues to cover <see cref="JobRelevanceWindow"/>. Under heavy Helix load this many jobs may
    /// span a shorter period than that, which just means we see a queue's backlog as younger than it really is --
    /// busy queues still rank behind idle ones, we're only less precise about by how much.
    /// </summary>
    private const string RecentJobsUrl = $"https://helix.dot.net/api/jobs?count=1000&{ApiVersion}";

    /// <summary>Work item created by Helix itself. It doesn't wait for the queue's machines.</summary>
    private const string ControllerWorkItemName = "HelixController Work Queueing";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>Only look at jobs submitted within this window.</summary>
    private static readonly TimeSpan JobRelevanceWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// A work item that started longer ago than this tells us nothing about the state the queue is in now.
    /// Work items that are still waiting always count, however long ago they were queued.
    /// </summary>
    private static readonly TimeSpan RecentStartWindow = TimeSpan.FromMinutes(15);

    /// <summary>How many jobs we sample per queue. Bounds how many requests we make against the Helix API.</summary>
    private const int MaxJobsSampledPerQueue = 6;

    /// <summary>Only move off the default queue if an alternative is at least this much faster.</summary>
    private static readonly TimeSpan MinimumImprovement = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly Logger _logger;

    /// <summary>Last observed delay of every queue we track. Replaced wholesale on every refresh.</summary>
    private volatile Dictionary<string, TimeSpan> _estimatedDelays = [];

    public HelixAvailabilityService(HttpClient http, Logger logger)
        : base(new PeriodicTaskOptions
        {
            Interval = RefreshInterval,
            RunImmediately = true,
            FailureBackoff = TimeSpan.Zero,
        }, logger)
    {
        _http = http;
        _logger = logger;
    }

    protected override async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, TimeSpan> delays = await GetEstimatedDelaysAsync(cancellationToken);

        _estimatedDelays = delays;

        _logger.DebugLog($"{nameof(HelixAvailabilityService)}: {(delays.Count == 0
            ? "no recent work seen on any queue"
            : string.Join(", ", delays.OrderBy(delay => delay.Value).Select(delay => $"{delay.Key}=~{delay.Value.ToElapsedTime()}")))}");
    }

    private async Task<Dictionary<string, TimeSpan>> GetEstimatedDelaysAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RefreshInterval);
        cancellationToken = timeoutCts.Token;

        HashSet<string> trackedQueues = GetTrackedQueues();

        HelixQueueInfoResponse[] queueInfos = await _http.GetFromJsonAsync<HelixQueueInfoResponse[]>(QueueInfoUrl, cancellationToken);

        if (queueInfos is null || queueInfos.Length == 0)
        {
            throw new InvalidOperationException("Helix returned no queue information");
        }

        HashSet<string> usableQueues = new(
            queueInfos
                .Where(info => !string.IsNullOrEmpty(info.QueueId) && trackedQueues.Contains(info.QueueId))
                // A scaleset that can't spin up any machines for us, or one that's throttling work from
                // sources it doesn't recognize, is a queue we can't use at all.
                .Where(info => info.IsAvailable && !info.IsInternalOnly && !info.IsInProtectedMode && info.ScaleMax > 0)
                .Select(info => info.QueueId),
            StringComparer.OrdinalIgnoreCase);

        // Queues we saw no recent work on are left out entirely -- an idle queue may just as well be a cold one,
        // and we have no way of telling how long it would take to spin a machine up for us.
        return await GetObservedWaitsAsync(usableQueues, cancellationToken);
    }

    /// <summary>
    /// Looks at recently submitted Helix jobs and returns the longest wait we can see on each queue.
    /// </summary>
    private async Task<Dictionary<string, TimeSpan>> GetObservedWaitsAsync(HashSet<string> queues, CancellationToken cancellationToken)
    {
        HelixJobSummaryResponse[] recentJobs = await _http.GetFromJsonAsync<HelixJobSummaryResponse[]>(RecentJobsUrl, cancellationToken);

        Dictionary<string, TimeSpan> waits = new(StringComparer.OrdinalIgnoreCase);

        if (recentJobs is null)
        {
            return waits;
        }

        DateTime oldestRelevantJob = DateTime.UtcNow - JobRelevanceWindow;

        IEnumerable<HelixJobSummaryResponse> jobsToSample = recentJobs
            .Where(job => !string.IsNullOrEmpty(job.Name) && !string.IsNullOrEmpty(job.QueueId))
            .Where(job => queues.Contains(job.QueueId) && job.Created.UtcDateTime >= oldestRelevantJob)
            .GroupBy(job => job.QueueId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(SelectJobsToSample);

        foreach (HelixJobSummaryResponse job in jobsToSample)
        {
            try
            {
                if (await GetWorkItemWaitAsync(job.Name, cancellationToken) is { } wait &&
                    wait > waits.GetValueOrDefault(job.QueueId))
                {
                    waits[job.QueueId] = wait;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.DebugLog($"{nameof(HelixAvailabilityService)}: failed to sample job '{job.Name}': {ex.Message}");
            }
        }

        return waits;
    }

    /// <summary>
    /// The oldest jobs tell us whether the queue is backed up, the newest what it's handing out right now.
    /// Sampling only the newest ones would let a few fast jobs hide an existing backlog.
    /// </summary>
    private static IEnumerable<HelixJobSummaryResponse> SelectJobsToSample(IEnumerable<HelixJobSummaryResponse> jobs)
    {
        HelixJobSummaryResponse[] ordered = [.. jobs.OrderBy(job => job.Created)];

        return ordered
            .Take(MaxJobsSampledPerQueue / 2)
            .Concat(ordered.TakeLast(MaxJobsSampledPerQueue / 2))
            .DistinctBy(job => job.Name);
    }

    /// <summary>
    /// How long one of the job's work items waited before it started, or has been waiting for so far.
    /// </summary>
    private async Task<TimeSpan?> GetWorkItemWaitAsync(string jobName, CancellationToken cancellationToken)
    {
        HelixWorkItemSummaryResponse[] workItems = await _http.GetFromJsonAsync<HelixWorkItemSummaryResponse[]>(
            $"https://helix.dot.net/api/jobs/{Uri.EscapeDataString(jobName)}/workitems?{ApiVersion}", cancellationToken);

        // Any work item is representative -- they all wait for the same queue.
        HelixWorkItemSummaryResponse workItem = workItems?
            .FirstOrDefault(w => !string.IsNullOrEmpty(w.DetailsUrl) && w.Name != ControllerWorkItemName);

        if (workItem is null)
        {
            return null;
        }

        HelixWorkItemDetailsResponse details = await _http.GetFromJsonAsync<HelixWorkItemDetailsResponse>(workItem.DetailsUrl, cancellationToken);

        if (details?.Queued is not { } queued)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;

        if (details.Started is not { } started)
        {
            // The work item hasn't started yet, so it has been waiting for at least this long.
            return now - queued.UtcDateTime;
        }

        // Ignore delays measured a while ago -- they say nothing about the state the queue is in now.
        return now - started.UtcDateTime > RecentStartWindow
            ? null
            : started.UtcDateTime - queued.UtcDateTime;
    }

    /// <summary>
    /// Returns the queue from <paramref name="candidates"/> that is expected to start running our work item the soonest.
    /// The first candidate is treated as the default and is only replaced if an alternative is meaningfully faster.
    /// </summary>
    public string SelectQueue(IReadOnlyList<string> candidates, out string explanation)
    {
        ArgumentOutOfRangeException.ThrowIfZero(candidates.Count);

        Dictionary<string, TimeSpan> delays = _estimatedDelays;
        string preferred = candidates[0];

        List<(string QueueId, TimeSpan Estimate)> ranked =
        [
            .. candidates
                .Where(delays.ContainsKey)
                .Select(queueId => (QueueId: queueId, Estimate: delays[queueId]))
                .OrderBy(entry => entry.Estimate)
        ];

        if (ranked.Count == 0)
        {
            explanation = $"no availability information for {string.Join(", ", candidates)}, using the default queue";
            return preferred;
        }

        (string bestQueue, TimeSpan bestEstimate) = ranked[0];

        string summary = string.Join(", ", ranked.Select(r => $"{r.QueueId}=~{r.Estimate.ToElapsedTime()}"));

        if (delays.TryGetValue(preferred, out TimeSpan estimate) &&
            estimate - bestEstimate < MinimumImprovement)
        {
            explanation = $"using the default queue ({summary})";
            return preferred;
        }

        explanation = $"'{bestQueue}' should be available sooner than the default '{preferred}' ({summary})";
        return bestQueue;
    }

    /// <inheritdoc cref="SelectQueue(IReadOnlyList{string}, out string)"/>
    public string SelectQueue(bool useWindows, bool useArm, out string explanation) =>
        SelectQueue(GetCandidateQueues(useWindows, useArm), out explanation);

    /// <summary>The queues a job may run on, in order of preference.</summary>
    /// <remarks>
    /// The '.rt' and '.svc' variants run the same image as the queue they're named after, just on a different
    /// scaleset, so anything that works on the base queue works on them. Some of them are a lot larger (the
    /// '.open.rt' Ubuntu queues scale to 1200 machines instead of 200), which makes them less likely to be busy.
    /// </remarks>
    private static string[] GetCandidateQueues(bool useWindows, bool useArm) => (useWindows, useArm) switch
    {
        // Besides git and the dotnet install script, the runner needs an image new enough to build dotnet/runtime.
        // Note that non-Ubuntu Linux images are excluded as our startup script relies on apt, and that
        // Ubuntu 22.04 is excluded as it only ships CMake 3.22 while the CoreCLR build requires 3.26 or higher.
        // Ubuntu 26.04 is excluded for the opposite reason: its toolchain is ahead of the product. Under C23
        // glibc defines the <string.h> search functions as const-generic macros, so 'strrchr' on a
        // 'const char *' now yields a 'const char *' and dotnet/runtime's vendored libunwind stops compiling
        // under -Werror. Current main is affected just as much as older commits, so a job scheduled there
        // fails every single build rather than only some.
        // Restore these once dotnet/runtime builds on 26.04 (see also dotnet/runtime#127334 for its build.sh).
        (false, false) =>
        [
            "ubuntu.2404.amd64.open",
            "ubuntu.2404.amd64.open.rt",
        ],
        (false, true) =>
        [
            "ubuntu.2404.armarch.open",
        ],
        // Plain Windows Server images are excluded as the startup script needs winget to install git.
        (true, false) =>
        [
            "windows.amd64.vs2022.open",
            "windows.amd64.vs2022.open.svc",
            "windows.amd64.vs2026.open",
            "windows.11.amd64.client.open",
            "windows.11.amd64.client.open.rt",
            "windows.11.amd64.client.open.svc",
        ],
        (true, true) =>
        [
            "windows.11.arm64.open",
            "windows.11.arm64.ampere.open",
        ],
    };

    private static HashSet<string> GetTrackedQueues() => new(
        [
            .. GetCandidateQueues(false, false),
            .. GetCandidateQueues(false, true),
            .. GetCandidateQueues(true, false),
            .. GetCandidateQueues(true, true)
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Subset of the Helix 'info/queues' response that we care about.</summary>
    private sealed class HelixQueueInfoResponse
    {
        public string QueueId { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsInternalOnly { get; set; }
        public bool IsInProtectedMode { get; set; }
        public int ScaleMax { get; set; }
    }

    private sealed class HelixJobSummaryResponse
    {
        public string QueueId { get; set; }
        public string Name { get; set; }
        public DateTimeOffset Created { get; set; }
    }

    private sealed class HelixWorkItemSummaryResponse
    {
        public string DetailsUrl { get; set; }
        public string Name { get; set; }
    }

    private sealed class HelixWorkItemDetailsResponse
    {
        public DateTimeOffset? Queued { get; set; }
        public DateTimeOffset? Started { get; set; }
    }
}
