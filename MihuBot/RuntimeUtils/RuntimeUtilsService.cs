using System.Buffers;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Storage.Blobs;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.Helpers.Cloud;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using MihuBot.RuntimeUtils.Jobs;
using MihuBot.Storage;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed partial class RuntimeUtilsService : IHostedService
{
    private enum SubmittedJobSource
    {
        Rest,
        Mcp,
        Web,
    }

    private const int MaxSubmittedPatchLength = 10 * 1024 * 1024;
    private const int MaxSubmittedArgumentsLength = 10 * 1024;
    private const int MaxConcurrentSubmittedJobs = 100;
    private const long MaxSubmittedPatchMemoryBytes = 2L * 1024 * 1024 * 1024;

    private static readonly SearchValues<char> s_submittedArgumentChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ._:/,#=*?+-");

    // https://gist.github.com/MihaZupan/6bcaacbe025265aa457ea7bb9a4dbcae
    private const string UsageCommentMarkdown =
        """
        ## Runtime-utils jobs

        Runtime-utils compares a tested dotnet/runtime change with a baseline and publishes logs, artifacts, and a tracking issue.

        Ways to submit:

        - Mention `@MihuBot` on a dotnet/runtime pull request.
        - Use the form at <https://mihubot.xyz/runtime-utils> for a pull request, branch, or public GitHub commit.
        - Agents can use the public REST API or the MCP tools `start_runtime_utils_patch_job` and
          `start_runtime_utils_commit_job`. Patch/commit API submissions currently support JitDiff,
          BenchmarkLibraries, and RegexDiff.

        Anonymous API/MCP submissions run on public Helix infrastructure. A GitHub access token supplied with the
        bearer authentication scheme identifies the caller; callers authorized to use runtime-utils may use Azure.
        The token is used for identity only and is not sent to the runner.


        <details>
        <summary>Common options and machine selection</summary>

        ```
        Options:
            -?|-help              Show help information

            -dependsOn <prs>      A comma-separated list of PR numbers to merge into the baseline branch.
            -combineWith <prs>    A comma-separated list of PR numbers to merge into the tested PR branch.

            -arm                  Use ARM64 instead of X64.
            -intel                Prefer an Intel CPU instead of AMD (Azure/Hetzner only).
            -win                  Use Windows. This currently implies Helix.
            -fast                 Use a larger paid VM. Has no effect on Helix machine selection.
            -hetzner              Run on a Hetzner VM instead of Azure.
            -helix                Run on a public Helix queue instead.
            -queue <queueId>      Run on a specific Helix queue (requires that -helix also be set).
                                  Defaults to whichever known queue is expected to become available soonest.

        Example:
            @MihuBot -arm -hetzner -combineWith #1000,#1001
            @MihuBot -helix -queue ubuntu.2404.armarch.open -arm
            @MihuBot -win -arm -queue windows.11.arm64.ampere.open
        ```

        `-queue` is an advanced option. The selected queue must be compatible with the job and runner startup scripts.
        macOS queues are not currently supported; the runner requires Linux or Windows. Linux Helix work items run
        inside a Linux container, so selecting a different Linux host queue does not change the guest OS.

        </details>


        <details>
        <summary>Generate JIT diffs</summary>

        ```
        Usage: @MihuBot [options]

        Options:
            -nocctors             Avoid passing --cctors to jit-diff.
            -tier0                Generate tier0 code.
            -nuget                Diff a larger set of popular NuGet packages (400+).

            -includeKnownNoise    Display diffs affected by known noise (e.g. race conditions between different JIT runs).
            -includeNewMethodRegressions        Display diffs for new methods.
            -includeRemovedMethodImprovements   Display diffs for removed methods.

        Example:
            @MihuBot
            @MihuBot -arm -tier0
        ```

        </details>


        <details>
        <summary>Run libraries benchmarks</summary>

        ```
        @MihuBot benchmark <benchmarks filter> [options]

        Options:
            <link to a GitHub commit diff (compare)>
            <link to a custom dotnet/performance branch>
            -medium               Use BenchmarkDotNet's medium run configuration.
            -long                 Use BenchmarkDotNet's long run configuration.

        Example:
            @MihuBot benchmark Regex
            @MihuBot benchmark GetUnicodeCategory https://github.com/dotnet/runtime/compare/4bb0bcd9b5c47df97e51b462d8204d66c7d470fc...c74440f8291edd35843f3039754b887afe61766e
            @MihuBot benchmark RustLang_Sherlock https://github.com/MihaZupan/performance/tree/compiled-regex-only -intel -medium
        ```

        Benchmark duration depends heavily on the filter, selected run configuration, and available hardware.
        `-medium` is recommended for more stable results, but avoid combining it with a broad filter: the job may
        take several hours or exceed its time limit. Start with the narrowest filter that covers the scenario.

        </details>


        <details>
        <summary>Run libraries fuzzer</summary>

        ```
        @MihuBot fuzz <fuzzer name pattern>

        Example:
            @MihuBot fuzz SearchValues
            @MihuBot fuzz SearchValues -dependsOn #107206

        The pattern may match multiple fuzzers (falls back to a Regex match).
        ```

        </details>


        <details>
        <summary>Generate Regex source generator code and JIT diffs</summary>

        ```
        @MihuBot regexdiff [options]

        Options:
            -jitdiff              Also generate JIT assembly diffs for changed regexes.

        Example:
            @MihuBot regexdiff
            @MihuBot regexdiff -arm -jitdiff
        ```

        </details>


        <details>
        <summary>Merge / Rebase / Format JIT changes</summary>

        ```
        @MihuBot merge/rebase/format    Requires collaborator access on your fork
        ```

        </details>


        <details>
        <summary>REST API and MCP automation</summary>

        Submit `POST https://mihubot.xyz/api/RuntimeUtils/Jobs` with JSON containing:

        - `jobType`: `JitDiff`, `BenchmarkLibraries`, or `RegexDiff`.
        - Exactly one of:
          - `patch`: a git-compatible unified diff, plus optional `baseRepository` and `baseBranch`.
          - `commit`: a public GitHub non-merge commit URL, compared with its parent.
        - `arguments`: the same job arguments documented above.

        The response includes the public job ID, dashboard URL, status API URL, finite logs API URL, and runner policy.
        Poll the status URL until `state` is `succeeded`, `failed`, or `cancelled`.

        The logs API returns the current log and closes by default:

        ```
        GET /api/RuntimeUtils/Jobs/Progress?jobId=<id>
        GET /api/RuntimeUtils/Jobs/Progress?jobId=<id>&tail=200
        GET /api/RuntimeUtils/Jobs/Progress?jobId=<id>&tail=200&live=true
        ```

        `live=true` keeps the response open while an active job runs. Avoid it for agents or tools that expect a
        finite HTTP response. Completed jobs always return their entire stored log.

        MCP provides `start_runtime_utils_patch_job`, `start_runtime_utils_commit_job`, and
        `get_runtime_utils_job_status`. The status tool can include all retained logs with `includeLogs=true`, or
        only recent lines with `tail=N`.

        </details>
        """;

    private readonly Dictionary<string, JobBase> _jobs = new(StringComparer.Ordinal);
    private readonly List<(string RunnerId, RunnerCapabilities Capabilities, TaskCompletionSource<string> RunnerAnnounceTCS)> _availableRunners = [];
    private readonly FileBackedHashSet _processedMentions = new("ProcessedMentionComments.txt");

    private static readonly MarkdownPipeline s_precisePipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    public readonly Logger Logger;
    public readonly GitHubClient Github;
    public readonly GitHubNotificationsService _gitHubNotifications;
    public readonly HttpClient Http;
    public readonly IConfiguration Configuration;
    public readonly IConfigurationService ConfigurationService;
    public readonly ServiceConfiguration ServiceConfiguration;
    /// <summary>Null when no Hetzner API key is configured.</summary>
    public readonly HetznerClient Hetzner;
    public readonly UrlShortenerService UrlShortener;
    public readonly CoreRootService CoreRoot;
    public readonly HelixAvailabilityService HelixAvailability;

    public readonly StorageService Storage;
    public StorageClient LogsStorage => _logsStorage.Value;
    public StorageClient ArtifactsStorage => _artifactsStorage.Value;
    public StorageClient RunnerPersistentStorage => _runnerPersistentStorage.Value;

    private readonly Lazy<StorageClient> _logsStorage;
    private readonly Lazy<StorageClient> _artifactsStorage;
    private readonly Lazy<StorageClient> _runnerPersistentStorage;

    /// <summary>Null when AzureStorage isn't configured.</summary>
    public readonly BlobContainerClient FuzzCoverageBlobContainerClient;
    /// <summary>Null when AzureStorage isn't configured.</summary>
    public readonly BlobContainerClient JitDiffExtraAssembliesBlobContainerClient;
    private readonly IDbContextFactory<MihuBotDbContext> _mihuBotDb;
    private readonly IDbContextFactory<GitHubDbContext> _gitHubDataDb;

    private bool _shuttingDown;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Lock _submittedJobsLock = new();
    private int _activeSubmittedJobs;
    private long _submittedPatchMemoryBytes;

    public RuntimeUtilsService(Logger logger, GitHubClient github, GitHubNotificationsService gitHubNotifications, HttpClient http, IConfiguration configuration, IConfigurationService configurationService, IEnumerable<HetznerClient> hetznerClients, IDbContextFactory<MihuBotDbContext> mihuBotDb, UrlShortenerService urlShortener, CoreRootService coreRoot, IDbContextFactory<GitHubDbContext> gitHubDataDb, ServiceConfiguration serviceConfiguration, StorageService storage, HelixAvailabilityService helixAvailability)
    {
        Logger = logger;
        Github = github;
        _gitHubNotifications = gitHubNotifications;
        Http = http;
        Configuration = configuration;
        ConfigurationService = configurationService;
        ServiceConfiguration = serviceConfiguration;
        Hetzner = hetznerClients.FirstOrDefault();
        UrlShortener = urlShortener;
        CoreRoot = coreRoot;
        HelixAvailability = helixAvailability;
        Storage = storage;

        _mihuBotDb = mihuBotDb;
        _gitHubDataDb = gitHubDataDb;

        _logsStorage = new Lazy<StorageClient>(() => CreateStorageClient(storage, http, "runtimeutils-logs", owner: "runtime-utils", isPublic: true, TimeSpan.FromDays(365 * 2)));
        _artifactsStorage = new Lazy<StorageClient>(() => CreateStorageClient(storage, http, "artifacts", owner: "runtime-utils", isPublic: true, TimeSpan.FromDays(60)));
        _runnerPersistentStorage = new Lazy<StorageClient>(() => CreateStorageClient(storage, http, "runner-persistent", owner: "runtime-utils", isPublic: false, TimeSpan.FromDays(90)));

        if (configuration.IsConfigured(OptionalFeatures.AzureStorageRuntimeUtils))
        {
            FuzzCoverageBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "artifacts");

            JitDiffExtraAssembliesBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "jitdiff-extra-assemblies");
        }

        static StorageClient CreateStorageClient(StorageService storage, HttpClient http, string name, string owner, bool isPublic, TimeSpan retention)
        {
            ContainerDbEntry entry = storage.EnsureContainerAsync(name, owner, isPublic, retention).GetAwaiter().GetResult();
            return new StorageClient(http, name, entry.SasKey, isPublic);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            PeriodicTask.Start(nameof(WatchForGitHubMentionsAsync),
                new PeriodicTaskOptions { Interval = TimeSpan.FromSeconds(1), FailureBackoff = TimeSpan.Zero },
                Logger, WatchForGitHubMentionsAsync, _shutdownCts.Token);

            PeriodicTask.Start(nameof(StartCoreRootGenerationJobsAsync),
                new PeriodicTaskOptions { Interval = TimeSpan.FromHours(8), FailureBackoff = TimeSpan.Zero },
                Logger, StartCoreRootGenerationJobsAsync, _shutdownCts.Token);

            PeriodicTask.Start(nameof(StartNuGetExtraAssembliesJobsAsync),
                new PeriodicTaskOptions { Interval = TimeSpan.FromDays(7), FailureBackoff = TimeSpan.Zero },
                Logger, StartNuGetExtraAssembliesJobsAsync, _shutdownCts.Token);

            PeriodicTask.Start(nameof(WatchForNegativeMihuBotCommentSentimentAsync),
                new PeriodicTaskOptions { Interval = TimeSpan.FromHours(4), FailureBackoff = TimeSpan.Zero },
                Logger, WatchForNegativeMihuBotCommentSentimentAsync, _shutdownCts.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the first feature that isn't configured, if any.
    /// </summary>
    public OptionalFeature GetMissingFeature(params OptionalFeature[] features) =>
        features.FirstOrDefault(feature => feature.RuntimeConfiguration
            ? !ConfigurationService.IsConfigured(feature)
            : !Configuration.IsConfigured(feature));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shuttingDown = true;

        await _shutdownCts.CancelAsync();

        JobBase[] activeJobs = GetAllActiveJobs();

        foreach (JobBase job in activeJobs)
        {
            job.FailFast("MihuBot is restarting", cancelledByAuthor: false);
        }

        if (activeJobs.Length > 0)
        {
            // Delay shutdown to give jobs time to delete any cloud resources / save state.
            await Task.Delay(10_000, cancellationToken);
        }
    }

    private async Task StartCoreRootGenerationJobsAsync(CancellationToken cancellationToken)
    {
        foreach (bool isArm in new[] { true, false })
        {
            try
            {
                if (GetAllActiveJobs().Any(j => j is CoreRootGenerationJob job && job.UseArm == isArm))
                {
                    Logger.DebugLog($"Skipping CoreRoot generation job {nameof(isArm)}={isArm}, one is already running");
                }
                else
                {
                    StartCoreRootGenerationJob("MihuBot", $"{(isArm ? "-arm" : "")} -automated");
                }
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync(nameof(StartCoreRootGenerationJobsAsync), ex);
            }
        }
    }

    private Task StartNuGetExtraAssembliesJobsAsync(CancellationToken cancellationToken)
    {
        if (GetAllActiveJobs().Any(j => j is NuGetExtraAssembliesJob))
        {
            Logger.DebugLog("Skipping NuGetExtraAssemblies job, one is already running");
        }
        else
        {
            StartNuGetExtraAssembliesJob("MihuBot", "-automated");
        }

        return Task.CompletedTask;
    }

    private async Task WatchForGitHubMentionsAsync(CancellationToken cancellationToken)
    {
        if (_shuttingDown)
        {
            return;
        }

        await using GitHubDbContext db = _gitHubDataDb.CreateDbContext();

        DateTime lastScan = DateTime.UtcNow;

        Stopwatch queryStopwatch = Stopwatch.StartNew();

#pragma warning disable CA1847 // Use char literal for a single character lookup -- EF doesn't support that
        var comments = await db.Comments
            .AsNoTracking()
            .Where(c => c.UpdatedAt >= lastScan - TimeSpan.FromMinutes(5))
            .OrderByDescending(c => c.UpdatedAt)
            .Where(c => c.Body.Contains("@"))
            .Where(c => c.Issue.IssueType != IssueType.Discussion)
            .Include(c => c.Issue)
                .ThenInclude(i => i.Repository)
                    .ThenInclude(r => r.Owner)
            .Include(c => c.Issue)
                .ThenInclude(i => i.PullRequest)
            .Include(c => c.User)
            .Take(25)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
#pragma warning restore CA1847

        ServiceInfo.LastGitHubCommentMentionsQueryTime = queryStopwatch.Elapsed;

        foreach (CommentInfo comment in comments)
        {
            if (IsDotnetRuntimeRepo(comment) || IsDotnetYarpRepo(comment))
            {
                await ProcessCommentAsync(comment);
            }
        }

        bool IsDotnetRuntimeRepo(CommentInfo comment) =>
            (comment.RepoOwner() == "dotnet" && comment.RepoName() == "runtime") ||
            ConfigurationService.GetOrDefault(null, $"RuntimeUtils.WatchRuntimeMentions.{comment.Issue.Repository.FullName}", false);

        bool IsDotnetYarpRepo(CommentInfo comment) =>
            comment.RepoOwner() == "dotnet" && comment.RepoName() == "yarp";

        async Task ProcessCommentAsync(CommentInfo comment)
        {
            try
            {
                if (comment.Body.Contains('@'))
                {
                    await _gitHubNotifications.ProcessGitHubMentionAsync(comment);
                }

                if (comment.Body.Contains("@MihuBot", StringComparison.OrdinalIgnoreCase) &&
                    !comment.User.Login.Equals("MihuBot", StringComparison.OrdinalIgnoreCase) &&
                    _processedMentions.TryAdd(comment.Id) &&
                    TryExtractMihuBotArguments(comment.Body, out string arguments))
                {
                    Logger.DebugLog($"Processing mention from {comment.User.Login} in {comment.HtmlUrl}: '{comment.Body}'");

                    if (arguments.Contains("-help", StringComparison.OrdinalIgnoreCase) ||
                        arguments.StartsWith("help", StringComparison.OrdinalIgnoreCase) ||
                        arguments is "-h" or "-H" or "?" or "-?")
                    {
                        await ReplyToCommentAsync(comment, UsageCommentMarkdown);
                        return;
                    }

                    if (!CheckGitHubCommenterPermissions(comment))
                    {
                        if (!comment.User.Login.Equals("msftbot", StringComparison.OrdinalIgnoreCase) &&
                            !comment.User.Login.Contains("dotnet-policy-service", StringComparison.OrdinalIgnoreCase))
                        {
                            await Logger.DebugAsync(
                                $"""
                                User {comment.User.Login} tried to start a job, but is not authorized. <{comment.HtmlUrl}>

                                `!cfg set global RuntimeUtils.AuthorizedUser.{comment.User.Login} true`
                                """);
                        }
                        return;
                    }

                    if (comment.Issue.PullRequest is not null)
                    {
                        PullRequest pullRequest = await Github.PullRequest.Get(comment.Issue.RepositoryId, comment.Issue.Number);

                        if (IsDotnetRuntimeRepo(comment))
                        {
                            await ProcessMihuBotDotnetRuntimeMention(comment, arguments, pullRequest);
                        }
                        else if (IsDotnetYarpRepo(comment))
                        {
                            await ProcessMihuBotYarpMention(comment, arguments, pullRequest);
                        }
                    }
                    else
                    {
                        if (IsDotnetRuntimeRepo(comment))
                        {
                            await ProcessMihuBotDotnetRuntimeIssueMention(comment, arguments);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync($"Failure while processing comment {comment.HtmlUrl}", ex);
            }
        }

        Task ProcessMihuBotYarpMention(CommentInfo comment, string arguments, PullRequest pullRequest)
        {
            if (arguments.StartsWith("backport to ", StringComparison.OrdinalIgnoreCase))
            {
                StartBackportJob(pullRequest, comment.User.Login, arguments, comment);
            }

            return Task.CompletedTask;
        }

        async Task ProcessMihuBotDotnetRuntimeIssueMention(CommentInfo comment, string arguments)
        {
            if (BenchmarkWithCompareRangeRegex().Match(arguments) is { Success: true } benchmarkMatch)
            {
                StartBenchmarkJob(comment.User.Login, arguments, comment);
                await TryAddEyesReaction(comment);
            }
        }

        async Task ProcessMihuBotDotnetRuntimeMention(CommentInfo comment, string arguments, PullRequest pullRequest)
        {
            if (pullRequest.State.Value != ItemState.Open)
            {
                return;
            }

            var fuzzMatch = FuzzMatchRegex().Match(arguments);
            var benchmarksMatch = BenchmarkFilterNameRegex().Match(arguments);

            if (fuzzMatch.Success)
            {
                StartFuzzLibrariesJob(pullRequest, comment.User.Login, arguments, comment);
            }
            else if (arguments.StartsWith("fuzz", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyToCommentAsync(comment, "Usage: `@MihuBot fuzz <fuzzer name pattern>`");
            }
            else if (benchmarksMatch.Success && benchmarksMatch.Groups[1].Value != "*")
            {
                StartBenchmarkJob(pullRequest, comment.User.Login, arguments, comment);
            }
            else if (arguments.StartsWith("benchmarks", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyToCommentAsync(comment, "Usage: `@MihuBot benchmark <benchmarks filter>`");
            }
            else if (
                arguments.StartsWith("regexdiff", StringComparison.OrdinalIgnoreCase) ||
                arguments.StartsWith("diffregex", StringComparison.OrdinalIgnoreCase))
            {
                StartRegexDiffJob(pullRequest, comment.User.Login, arguments, comment);
            }
            else if (
                arguments.StartsWith("rebase", StringComparison.OrdinalIgnoreCase) ||
                arguments.StartsWith("merge", StringComparison.OrdinalIgnoreCase) ||
                arguments.StartsWith("format", StringComparison.OrdinalIgnoreCase) ||
                arguments.StartsWith("jitformat", StringComparison.OrdinalIgnoreCase) ||
                arguments.StartsWith("jit-format", StringComparison.OrdinalIgnoreCase))
            {
                if ((await Github.Repository.Get(pullRequest.Head.Repository.Id)).Permissions.Push)
                {
                    StartRebaseJob(pullRequest, comment.User.Login, arguments, comment);
                }
                else
                {
                    await ReplyToCommentAsync(comment,
                        $"""
                        I don't have push access to your repository.
                        You can add me as a collaborator at https://github.com/{pullRequest.Head.Repository.FullName}/settings/access
                        """);

                    await Logger.DebugAsync($"User {comment.User.Login} requires collaborator access. <{pullRequest.HtmlUrl}>");
                }
            }
            else
            {
                StartJitDiffJob(pullRequest, comment.User.Login, arguments, comment);
            }

            await TryAddEyesReaction(comment);
        }

        async Task TryAddEyesReaction(CommentInfo comment)
        {
            try
            {
                await comment.AddReactionAsync(Github, Octokit.ReactionType.Eyes);
            }
            catch (Exception ex)
            {
                Logger.DebugLog($"Failed to add reaction to comment {comment.HtmlUrl}: {ex}");
            }
        }

        async Task ReplyToCommentAsync(CommentInfo comment, string content)
        {
            await Github.Issue.Comment.Create(comment.Issue.RepositoryId, comment.Issue.Number, content);
        }

        static bool TryExtractMihuBotArguments(string commentBody, out string arguments)
        {
            MarkdownDocument document = Markdown.Parse(commentBody, s_precisePipeline);

            int candidateOffset = -1;
            int offset = 0;

            while (true)
            {
                offset = commentBody.IndexOf("@MihuBot", offset, StringComparison.OrdinalIgnoreCase);
                if (offset < 0)
                {
                    break;
                }

                offset += "@MihuBot".Length;

                if (document.FindBlockAtPosition(offset) is not { } block)
                {
                    candidateOffset = offset;
                    continue;
                }

                if (block.Descendants<CodeInline>()
                    .Any(ci => ci.Span.Start <= offset && ci.Span.End >= offset))
                {
                    continue;
                }

                if (!IsInQuoteOrFencedCodeBlock(block))
                {
                    candidateOffset = offset;
                    break;
                }
            }

            if (candidateOffset >= 0)
            {
                arguments = commentBody.AsSpan(candidateOffset).Trim().ToString();
                return true;
            }

            arguments = null;
            return false;

            static bool IsInQuoteOrFencedCodeBlock(Block block)
            {
                while (block is not null)
                {
                    if (block is QuoteBlock or FencedCodeBlock)
                    {
                        return true;
                    }

                    block = block.Parent;
                }

                return false;
            }
        }
    }

    private User _sentimentCheckUser;
    private readonly Dictionary<string, int> _seenNegativeSentimentComments = [];

    private async Task WatchForNegativeMihuBotCommentSentimentAsync(CancellationToken cancellationToken)
    {
        if (_shuttingDown ||
            ConfigurationService.GetOrDefault(null, $"{nameof(WatchForNegativeMihuBotCommentSentimentAsync)}.Pause", false) ||
            ServiceConfiguration.PauseGitHubPolling)
        {
            return;
        }

        User currentUser = _sentimentCheckUser ??= await Github.User.Current();
        Dictionary<string, int> seenComments = _seenNegativeSentimentComments;

        await using GitHubDbContext db = _gitHubDataDb.CreateDbContext();

        DateTime startDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(2));

        var comments = await db.Comments
            .AsNoTracking()
            .Where(c => c.UpdatedAt >= startDate)
            .Where(c => c.UserId == currentUser.Id)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(100)
            .Select(c => new { c.Id, c.GitHubIdentifier, c.Issue.RepositoryId, c.HtmlUrl })
            .ToArrayAsync(cancellationToken);

        foreach (var comment in comments)
        {
            try
            {
                IssueComment updatedInfo = await Github.Issue.Comment.Get(comment.RepositoryId, comment.GitHubIdentifier);

                int newCount = updatedInfo.Reactions.Minus1 + updatedInfo.Reactions.Confused + updatedInfo.Reactions.Laugh;

                if (newCount == 0)
                {
                    continue;
                }

                if (!seenComments.TryGetValue(comment.Id, out int previousCount) ||
                    previousCount < newCount)
                {
                    seenComments[comment.Id] = newCount;

                    await Logger.DebugAsync($"Negative sentiment of {newCount} for <{updatedInfo.HtmlUrl}>");
                }
            }
            catch (Exception ex)
            {
                // Could have been transferred/deleted
                if (ex is not NotFoundException)
                {
                    await Logger.DebugAsync($"Failed to get updated info for <{comment.HtmlUrl}>", ex);
                }
            }

            await Task.Delay(1_000, cancellationToken);
        }
    }

    public bool TryGetJob(string jobId, bool publicId, out JobBase job)
    {
        lock (_jobs)
        {
            return _jobs.TryGetValue(jobId, out job) &&
                (publicId ? job.ExternalId : job.JobId) == jobId;
        }
    }

    public JobBase StartJitDiffJob(BranchReference branch, string githubCommenterLogin, string arguments) =>
        StartJobCore(new JitDiffJob(this, branch, githubCommenterLogin, arguments));

    public JobBase StartJitDiffJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new JitDiffJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartFuzzLibrariesJob(BranchReference branch, string githubCommenterLogin, string arguments) =>
        StartJobCore(new FuzzLibrariesJob(this, branch, githubCommenterLogin, arguments));

    public JobBase StartFuzzLibrariesJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new FuzzLibrariesJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartRebaseJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new RebaseJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartBenchmarkJob(BranchReference branch, string githubCommenterLogin, string arguments) =>
        StartJobCore(new BenchmarkLibrariesJob(this, branch, githubCommenterLogin, arguments));

    public JobBase StartBenchmarkJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new BenchmarkLibrariesJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartBenchmarkJob(string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new BenchmarkLibrariesJob(this, githubCommenterLogin, arguments, comment));

    public JobBase StartRegexDiffJob(BranchReference branch, string githubCommenterLogin, string arguments) =>
        StartJobCore(new RegexDiffJob(this, branch, githubCommenterLogin, arguments));

    public JobBase StartRegexDiffJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new RegexDiffJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public async Task<PatchJobSubmissionResponse> StartPatchJobAsync(PatchJobRequest request, string githubToken, CancellationToken cancellationToken)
    {
        ThrowIfApiSubmissionsDisabled();
        (string Login, long Id)? caller = await TryGetGitHubCallerAsync(githubToken, cancellationToken);
        return await StartPatchJobCoreAsync(request, caller, SubmittedJobSource.Rest, cancellationToken);
    }

    public async Task<PatchJobSubmissionResponse> StartPatchJobFromMcpAsync(PatchJobRequest request, string githubToken, CancellationToken cancellationToken)
    {
        ThrowIfApiSubmissionsDisabled();
        (string Login, long Id)? caller = await TryGetGitHubCallerAsync(githubToken, cancellationToken);
        return await StartPatchJobCoreAsync(request, caller, SubmittedJobSource.Mcp, cancellationToken);
    }

    public Task<PatchJobSubmissionResponse> StartPatchJobForGitHubUserAsync(PatchJobRequest request, string login, long id, CancellationToken cancellationToken) =>
        StartPatchJobCoreAsync(request, (login, id), SubmittedJobSource.Web, cancellationToken);

    private void ThrowIfApiSubmissionsDisabled()
    {
        if (ServiceConfiguration.DisableRuntimeUtilsApiSubmissions)
        {
            throw new RuntimeUtilsSubmissionsDisabledException();
        }
    }

    private async Task<PatchJobSubmissionResponse> StartPatchJobCoreAsync(
        PatchJobRequest request,
        (string Login, long Id)? caller,
        SubmittedJobSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.JobType?.Length > 64)
        {
            throw new ArgumentException("JobType must not exceed 64 characters.", nameof(request));
        }

        if (request.BaseRepository?.Length > 200)
        {
            throw new ArgumentException("BaseRepository must not exceed 200 characters.", nameof(request));
        }

        if (request.BaseBranch?.Length > 500)
        {
            throw new ArgumentException("BaseBranch must not exceed 500 characters.", nameof(request));
        }

        if (request.Commit?.Length > 500)
        {
            throw new ArgumentException("Commit must not exceed 500 characters.", nameof(request));
        }

        if (request.Arguments is { } submittedArguments &&
            Encoding.UTF8.GetByteCount(submittedArguments) > MaxSubmittedArgumentsLength)
        {
            throw new ArgumentException("Arguments must not exceed 10 KiB.", nameof(request));
        }

        bool hasPatch = !string.IsNullOrWhiteSpace(request.Patch);
        bool hasCommit = !string.IsNullOrWhiteSpace(request.Commit);
        if (hasPatch == hasCommit)
        {
            throw new ArgumentException("Specify exactly one of Patch or Commit.", nameof(request));
        }

        string repository = string.IsNullOrWhiteSpace(request.BaseRepository) ? "dotnet/runtime" : request.BaseRepository.Trim();
        string branch = string.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : request.BaseBranch.Trim();
        string baseCommit = null;
        string patch = request.Patch;
        string testedLink = null;
        string testedCommit = null;

        if (hasCommit)
        {
            if (!GitHubHelper.TryParseGitHubCommit(request.Commit.Trim(), out repository, out string commitSha))
            {
                throw new ArgumentException("Commit must be a public GitHub commit URL.", nameof(request));
            }

            string[] parts = repository.Split('/');
            string commitOwner = parts[0];
            string commitRepositoryName = parts[1];

            GitHubCommit commit;
            Repository commitRepository;
            try
            {
                commit = await Github.Repository.Commit.Get(commitOwner, commitRepositoryName, commitSha);
                commitRepository = await Github.Repository.Get(commitOwner, commitRepositoryName);
            }
            catch (NotFoundException ex)
            {
                throw new ArgumentException($"The commit '{request.Commit}' does not exist or is not public.", nameof(request), ex);
            }

            if (commit.Parents.Count != 1)
            {
                throw new ArgumentException("Commit jobs require a non-merge commit with exactly one parent.", nameof(request));
            }

            branch = commitRepository.DefaultBranch;
            baseCommit = commit.Parents[0].Sha;
            testedCommit = commit.Sha;
            testedLink = commit.HtmlUrl;
            patch = await DownloadCommitPatchAsync(repository, commit.Sha, cancellationToken);
        }

        if (patch.Length > MaxSubmittedPatchLength)
        {
            throw new ArgumentException("Patch must not exceed 10 MiB.", nameof(request));
        }

        if (!GitHubHelper.TryParseRepoOwnerAndName(repository, out string owner, out string name, out string[] extra) ||
            extra.Length != 0 ||
            !repository.Equals($"{owner}/{name}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("BaseRepository must be in owner/name form.", nameof(request));
        }

        if (!GitHubHelper.IsSafeGitHubBranchName(branch))
        {
            throw new ArgumentException("BaseBranch contains unsupported characters.", nameof(request));
        }

        string arguments = request.Arguments?.Trim() ?? string.Empty;
        if (arguments.AsSpan().ContainsAnyExcept(s_submittedArgumentChars))
        {
            throw new ArgumentException("Arguments contain unsupported shell characters.", nameof(request));
        }

        try
        {
            await Github.Repository.Branch.Get(owner, name, branch);
        }
        catch (NotFoundException ex)
        {
            throw new ArgumentException($"The branch '{repository}/{branch}' does not exist or is not public.", nameof(request), ex);
        }

        bool canUseAzure = caller is { } identity &&
            CheckGitHubUserPermissions("dotnet", identity.Login, identity.Id) == true;

        long patchMemoryBytes = checked((long)patch.Length * sizeof(char));
        SubmittedJobReservation reservation = ReserveSubmittedJob(patchMemoryBytes);
        try
        {
            JobBase job = request.JobType?.Trim().ToLowerInvariant() switch
            {
                "jitdiff" => new JitDiffJob(this, repository, branch, baseCommit, patch, testedLink, caller?.Login, arguments, forceHelix: !canUseAzure),
                "benchmark" or "benchmarklibraries" => new BenchmarkLibrariesJob(this, repository, branch, baseCommit, patch, testedLink, caller?.Login, arguments, forceHelix: !canUseAzure),
                "regexdiff" => new RegexDiffJob(this, repository, branch, baseCommit, patch, testedLink, caller?.Login, arguments, forceHelix: !canUseAzure),
                _ => throw new ArgumentException("JobType must be JitDiff, BenchmarkLibraries, or RegexDiff.", nameof(request)),
            };

            if (testedCommit is not null)
            {
                job.Metadata["PrBranch"] = testedCommit;
                job.Metadata.Add("TestedCommit", testedCommit);
            }

            if (source == SubmittedJobSource.Mcp)
            {
                job.Metadata.Add("StartedViaMcp", bool.TrueString);
            }

            StartJobCore(job, () =>
            {
                job.ReleasePatchContent();
                reservation.Dispose();
            });

            if (source is SubmittedJobSource.Rest or SubmittedJobSource.Mcp)
            {
                Logger.DebugLog(
                    $"Runtime-utils {source} submission started {job.GetType().Name} for {caller?.Login ?? "anonymous"}: " +
                    $"{job.ProgressDashboardUrl} (input={(hasCommit ? testedLink : "patch")}, " +
                    $"baseline={repository}/{baseCommit ?? branch}, patchSize={Encoding.UTF8.GetByteCount(patch)}, " +
                    $"runnerPolicy={(canUseAzure ? "azure-allowed" : "helix-required")}, " +
                    $"arguments='{arguments.TruncateWithDotDotDot(1_000)}')");
            }

            return new PatchJobSubmissionResponse(
                job.ExternalId,
                $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Status?jobId={job.ExternalId}",
                job.ProgressDashboardUrl,
                job.LogsUrl,
                canUseAzure ? "azure-allowed" : "helix-required");
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private async Task<string> DownloadCommitPatchAsync(string repository, string commitSha, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync($"https://github.com/{repository}/commit/{commitSha}.patch", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await response.Content.LoadIntoBufferAsync(MaxSubmittedPatchLength, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<(string Login, long Id)?> TryGetGitHubCallerAsync(string githubToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.UserAgent.ParseAdd("MihuBot-RuntimeUtils-API");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("The supplied GitHub token is invalid or cannot identify its owner.");
        }

        response.EnsureSuccessStatusCode();

        GitHubCallerResponse caller = await response.Content.ReadFromJsonAsync<GitHubCallerResponse>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty user response.");

        if (string.IsNullOrWhiteSpace(caller.Login) || caller.Id == 0)
        {
            throw new InvalidOperationException("GitHub returned an invalid user response.");
        }

        return (caller.Login, caller.Id);
    }

    private sealed record GitHubCallerResponse(string Login, long Id);

    public JobBase StartBackportJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new BackportJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartCoreRootGenerationJob(string githubCommenterLogin, string arguments) =>
        StartJobCore(new CoreRootGenerationJob(this, githubCommenterLogin, arguments));

    public JobBase StartNuGetExtraAssembliesJob(string githubCommenterLogin, string arguments) =>
        StartJobCore(new NuGetExtraAssembliesJob(this, githubCommenterLogin, arguments));

    public JobBase StartJobCore(JobBase job, Action onCompleted = null)
    {
        lock (_jobs)
        {
            if (_shuttingDown)
            {
                onCompleted?.Invoke();
                return job;
            }

            _jobs.Add(job.JobId, job);
            _jobs.Add(job.ExternalId, job);
        }

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromDays(2));

                lock (_jobs)
                {
                    _jobs.Remove(job.JobId);
                    _jobs.Remove(job.ExternalId);
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await job.RunJobAsync();
                }
                catch (Exception ex)
                {
                    await Logger.DebugAsync(ex.ToString());
                }
                finally
                {
                    onCompleted?.Invoke();
                }
            });
        }

        return job;
    }

    private SubmittedJobReservation ReserveSubmittedJob(long patchMemoryBytes)
    {
        lock (_submittedJobsLock)
        {
            if (_activeSubmittedJobs >= MaxConcurrentSubmittedJobs)
            {
                throw new RuntimeUtilsCapacityException($"At most {MaxConcurrentSubmittedJobs} submitted jobs may run concurrently.");
            }

            if (_submittedPatchMemoryBytes > MaxSubmittedPatchMemoryBytes - patchMemoryBytes)
            {
                throw new RuntimeUtilsCapacityException("Submitted jobs are already retaining the maximum 2 GiB of patch content.");
            }

            _activeSubmittedJobs++;
            _submittedPatchMemoryBytes += patchMemoryBytes;
            return new SubmittedJobReservation(this, patchMemoryBytes);
        }
    }

    private void ReleaseSubmittedJob(long patchMemoryBytes)
    {
        lock (_submittedJobsLock)
        {
            _activeSubmittedJobs--;
            _submittedPatchMemoryBytes -= patchMemoryBytes;
        }
    }

    private sealed class SubmittedJobReservation(RuntimeUtilsService owner, long patchMemoryBytes) : IDisposable
    {
        private RuntimeUtilsService _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseSubmittedJob(patchMemoryBytes);
        }
    }

    public JobBase[] GetAllActiveJobs()
    {
        lock (_jobs)
        {
            return _jobs
                .Where(pair => pair.Key == pair.Value.ExternalId)
                .Select(pair => pair.Value)
                .Where(job => !job.Completed)
                .OrderByDescending(job => job.Stopwatch.Elapsed)
                .ToArray();
        }
    }

    public async Task<string> AnnounceRunnerAsync(string runnerId, RunnerCapabilities capabilities, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_availableRunners)
        {
            _availableRunners.Add((runnerId, capabilities, tcs));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using (cts.Token.Register(static s => ((TaskCompletionSource<string>)s).TrySetCanceled(), tcs))
        {
            try
            {
                Logger.DebugLog($"Runner {runnerId} announced ({capabilities})");
                string jobId = await tcs.Task;
                Logger.DebugLog($"Runner {runnerId} got signaled");
                return jobId;
            }
            catch
            {
                Logger.DebugLog($"Runner {runnerId} announcement canceled");
                return null;
            }
            finally
            {
                lock (_availableRunners)
                {
                    _availableRunners.RemoveAll(static entry => entry.RunnerAnnounceTCS.Task.IsCompleted);
                }
            }
        }
    }

    public string TrySignalAvailableRunner(JobBase job)
    {
        RunnerCapabilities required = job.GetRequiredRunnerCapabilities();

        TaskCompletionSource<string> tcs = null;
        string runnerId = null;

        lock (_availableRunners)
        {
            _availableRunners.RemoveAll(static entry => entry.RunnerAnnounceTCS.Task.IsCompleted);

            int idx = _availableRunners.FindIndex(entry => required.IsCompatibleWith(entry.Capabilities));
            if (idx >= 0)
            {
                tcs = _availableRunners[idx].RunnerAnnounceTCS;
                runnerId = _availableRunners[idx].RunnerId;
                _availableRunners.RemoveAt(idx);
            }
        }

        return tcs?.TrySetResult(job.JobId) == true ? runnerId : null;
    }

    public bool? CheckGitHubUserPermissions(string repositoryOwner, string userLogin, long userId)
    {
        if (string.IsNullOrWhiteSpace(repositoryOwner) ||
            string.IsNullOrWhiteSpace(userLogin) ||
            userId == 0 ||
            ConfigurationService.GetOrDefault(null, $"RuntimeUtils.BlockedUser.{userId}", false) ||
            ConfigurationService.GetOrDefault(null, $"RuntimeUtils.BlockedUser.{userLogin}", false))
        {
            return false;
        }

        if (ConfigurationService.GetOrDefault(null, $"RuntimeUtils.AuthorizedUser.{userLogin}", false) ||
            ConfigurationService.GetOrDefault(null, $"RuntimeUtils.AuthorizedUser.{repositoryOwner}.{userLogin}", false))
        {
            return true;
        }

        if (userId == GitHubDataIngestionService.CopilotUserId &&
            ConfigurationService.GetOrDefault(null, $"RuntimeUtils.AllowCopilot.{repositoryOwner}", true))
        {
            return true;
        }

        if (CheckGitHubAdminPermissions(repositoryOwner))
        {
            return true;
        }

        return null;
    }

    public bool CheckGitHubCommenterPermissions(CommentInfo comment)
    {
        bool? result = CheckGitHubUserPermissions(comment.RepoOwner(), comment.User.Login, comment.UserId);

        if (result.HasValue)
        {
            return result.Value;
        }

        if (comment.AuthorAssociation is AuthorAssociation.Owner or AuthorAssociation.Member or AuthorAssociation.Collaborator &&
            ConfigurationService.GetOrDefault(null, $"RuntimeUtils.AllowCollaborators.{comment.RepoOwner()}", true))
        {
            return true;
        }

        return false;
    }

    public bool CheckGitHubAdminPermissions(string userLogin) =>
        ConfigurationService.GetOrDefault(null, $"RuntimeUtils.Admin.{userLogin}", false);

    public async Task<PullRequest> GetPullRequestAsync(int prNumber)
    {
        PullRequest pullRequest = await Github.PullRequest.Get("dotnet", "runtime", prNumber);
        ArgumentNullException.ThrowIfNull(pullRequest);
        return pullRequest;
    }

    public async Task<CompletedJobRecord> TryGetCompletedJobRecordAsync(string externalId, CancellationToken cancellationToken)
    {
        await using var context = _mihuBotDb.CreateDbContext();

        CompletedJobDbEntry entry = await context.CompletedJobs.FindAsync([externalId], cancellationToken: cancellationToken);

        try
        {
            return entry?.ToRecord();
        }
        catch (Exception ex)
        {
            Logger.DebugLog($"Failed to create completed job record for {externalId}: {ex}");
            throw;
        }
    }

    public async Task<RuntimeUtilsJobStatusResponse> TryGetJobStatusAsync(
        string externalId,
        CancellationToken cancellationToken,
        bool includeLogs = false,
        int? tail = null)
    {
        if (tail is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(tail), "Tail must be between 0 and 100000.");
        }

        includeLogs |= tail.HasValue;

        if (TryGetJob(externalId, publicId: true, out JobBase job))
        {
            string state =
                !job.Completed ? (job.InitialRemoteRunnerContact is null ? "queued" : "running") :
                job.WasCancelled ? "cancelled" :
                job.ErrorMessage is null ? "succeeded" :
                "failed";

            return new RuntimeUtilsJobStatusResponse(
                job.ExternalId,
                state,
                job.JobTitle,
                job.StartTime,
                job.Stopwatch.Elapsed,
                job.ProgressDashboardUrl,
                job.LogsUrl,
                job.TrackingIssue?.HtmlUrl,
                job.LastProgressSummary,
                job.ErrorMessage,
                job.GetArtifactsSnapshot(),
                includeLogs ? job.GetLogSnapshot(tail) : null);
        }

        if (await TryGetCompletedJobRecordAsync(externalId, cancellationToken) is not { } completed)
        {
            return null;
        }

        return new RuntimeUtilsJobStatusResponse(
            completed.ExternalId,
            completed.WasCancelled ? "cancelled" : completed.ErrorMessage is null ? "succeeded" : "failed",
            completed.Title,
            completed.StartedAt,
            completed.Duration,
            $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/runtime-utils/{completed.ExternalId}",
            $"https://{(Debugger.IsAttached ? "localhost" : "mihubot.xyz")}/api/RuntimeUtils/Jobs/Progress?jobId={completed.ExternalId}",
            completed.TrackingIssueUrl,
            null,
            completed.ErrorMessage,
            completed.Artifacts ?? [],
            includeLogs ? await GetCompletedJobLogsAsync(completed, tail, cancellationToken) : null);
    }

    internal async Task<string[]> GetCompletedJobLogsAsync(CompletedJobRecord completed, int? tail, CancellationToken cancellationToken)
    {
        if (completed.LogsArtifactUrl is null)
        {
            return [];
        }

        using HttpResponseMessage response = await Http.GetAsync(completed.LogsArtifactUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        if (tail is null)
        {
            var lines = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lines.Add(line);
            }

            return [.. lines];
        }

        var tailLines = new Queue<string>(tail.Value);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (tailLines.Count == tail.Value && tailLines.Count > 0)
            {
                tailLines.Dequeue();
            }

            if (tail.Value > 0)
            {
                tailLines.Enqueue(line);
            }
        }

        return [.. tailLines];
    }

    public async Task SaveCompletedJobRecordAsync(CompletedJobRecord record)
    {
        await using var context = _mihuBotDb.CreateDbContext();

        context.CompletedJobs.Add(record.ToDbEntry());

        await context.SaveChangesAsync();
    }

    [GeneratedRegex(@"^fuzz ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FuzzMatchRegex();

    [GeneratedRegex(@"^benchmark ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BenchmarkFilterNameRegex();

    // @MihuBot benchmark GetUnicodeCategory https://github.com/dotnet/runtime/compare/4bb0bcd9b5c47df97e51b462d8204d66c7d470fc...c74440f8291edd35843f3039754b887afe61766e
    [GeneratedRegex(@"^benchmark ([^ ]+) https:\/\/github\.com\/dotnet\/runtime\/compare\/([a-f0-9]{40}\.\.\.[a-f0-9]{40})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BenchmarkWithCompareRangeRegex();
}
