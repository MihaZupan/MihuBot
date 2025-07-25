﻿using Azure.Storage.Blobs;
using Markdig;
using Markdig.Syntax;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.Jobs;
using Octokit;
using System.Text.RegularExpressions;

namespace MihuBot.RuntimeUtils;

public sealed partial class RuntimeUtilsService : IHostedService
{
    private const string UsageCommentMarkdown =
        """
        <details>
        <summary>Extra options for most job types</summary>

        ```
        Options:
            -?|-help              Show help information

            -dependsOn <prs>      A comma-separated list of PR numbers to merge into the baseline branch.
            -combineWith <prs>    A comma-separated list of PR numbers to merge into the tested PR branch.

            -arm                  Run on an ARM64 VM instead of X64.
            -intel                Run on an Intel-based VM instead of an AMD-based one.
            -fast                 Run on a more powerful VM to save a few minutes.
            -hetzner              Run on a Hetzner VM instead of Azure.

        Example:
            @MihuBot -arm -hetzner -combineWith #1000,#1001
        ```

        </details>


        <details>
        <summary>Generate JIT diffs</summary>

        See <https://mihubot.xyz/runtime-utils> for an alternative way of submitting jobs.

        ```
        Usage: @MihuBot [options]

        Options:
            -nocctors             Avoid passing --cctors to jit-diff.
            -tier0                Generate tier0 code.

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
            -medium/long

        Example:
            @MihuBot benchmark Regex
            @MihuBot benchmark GetUnicodeCategory https://github.com/dotnet/runtime/compare/4bb0bcd9b5c47df97e51b462d8204d66c7d470fc...c74440f8291edd35843f3039754b887afe61766e
            @MihuBot benchmark RustLang_Sherlock https://github.com/MihaZupan/performance/tree/compiled-regex-only -intel -medium
        ```

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
        @MihuBot regexdiff

        Example:
            @MihuBot regexdiff
            @MihuBot regexdiff -arm
        ```

        </details>


        <details>
        <summary>Merge / Rebase / Format JIT changes</summary>

        ```
        @MihuBot merge/rebase/format    Requires collaborator access on your fork
        ```

        </details>
        """;

    private readonly Dictionary<string, JobBase> _jobs = new(StringComparer.Ordinal);
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
    public readonly HetznerClient Hetzner;
    public readonly BlobContainerClient ArtifactsBlobContainerClient;
    public readonly BlobContainerClient RunnerPersistentStateBlobContainerClient;
    public readonly BlobContainerClient JitDiffExtraAssembliesBlobContainerClient;
    public readonly UrlShortenerService UrlShortener;
    public readonly CoreRootService CoreRoot;
    public readonly StorageClient LogsStorage;
    private readonly IDbContextFactory<MihuBotDbContext> _mihuBotDb;
    private readonly IDbContextFactory<GitHubDbContext> _gitHubDataDb;

    private bool _shuttingDown;

    public RuntimeUtilsService(Logger logger, GitHubClient github, GitHubNotificationsService gitHubNotifications, HttpClient http, IConfiguration configuration, IConfigurationService configurationService, HetznerClient hetzner, IDbContextFactory<MihuBotDbContext> mihuBotDb, UrlShortenerService urlShortener, CoreRootService coreRoot, IDbContextFactory<GitHubDbContext> gitHubDataDb)
    {
        Logger = logger;
        Github = github;
        _gitHubNotifications = gitHubNotifications;
        Http = http;
        Configuration = configuration;
        ConfigurationService = configurationService;
        Hetzner = hetzner;
        UrlShortener = urlShortener;
        CoreRoot = coreRoot;

        _mihuBotDb = mihuBotDb;
        _gitHubDataDb = gitHubDataDb;

        if (ProgramState.AzureEnabled)
        {
            ArtifactsBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "artifacts");

            RunnerPersistentStateBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "runner-persistent");

            JitDiffExtraAssembliesBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "jitdiff-extra-assemblies");
        }

        if (!ConfigurationService.TryGet(null, "RuntimeUtils.JobLogs.SasKey", out string sasKey))
        {
            if (OperatingSystem.IsLinux())
            {
                throw new InvalidOperationException("Missing 'RuntimeUtils.JobLogs.SasKey'");
            }

            // For local testing
            sasKey = "";
        }

        LogsStorage = new StorageClient(Http, "runtimeutils-logs", sasKey, isPublic: true);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(WatchForGitHubMentionsAsync, CancellationToken.None);
                _ = Task.Run(StartCoreRootGenerationJobsAsync, CancellationToken.None);
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shuttingDown = true;

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

    private async Task StartCoreRootGenerationJobsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(8));

        while (await timer.WaitForNextTickAsync())
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
                        StartCoreRootGenerationJob("MihaZupan", $"{(isArm ? "-arm" : "")} -automated");
                    }
                }
                catch (Exception ex)
                {
                    await Logger.DebugAsync(nameof(StartCoreRootGenerationJobsAsync), ex);
                }
            }
        }
    }

    private async Task WatchForGitHubMentionsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        DateTime lastScan = DateTime.UtcNow;

        while (await timer.WaitForNextTickAsync())
        {
            if (_shuttingDown)
            {
                break;
            }

            try
            {
                await using GitHubDbContext db = _gitHubDataDb.CreateDbContext();

                lastScan = DateTime.UtcNow;

                Stopwatch queryStopwatch = Stopwatch.StartNew();

#pragma warning disable CA1847 // Use char literal for a single character lookup -- EF doesn't support that
                var comments = await db.Comments
                    .AsNoTracking()
                    .Where(c => c.UpdatedAt >= lastScan - TimeSpan.FromMinutes(5))
                    .OrderByDescending(c => c.UpdatedAt)
                    .Where(c => c.Body.Contains("@"))
                    .Include(c => c.Issue)
                        .ThenInclude(i => i.Repository)
                            .ThenInclude(r => r.Owner)
                    .Include(c => c.Issue)
                        .ThenInclude(i => i.PullRequest)
                    .Include(c => c.User)
                    .Take(25)
                    .AsSplitQuery()
                    .ToListAsync();
#pragma warning restore CA1847

                ServiceInfo.LastGitHubCommentMentionsQueryTime = queryStopwatch.Elapsed;

                foreach (CommentInfo comment in comments)
                {
                    if (comment.RepoOwner() == "dotnet" && comment.RepoName() is "runtime" or "yarp")
                    {
                        await ProcessCommentAsync(comment);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.DebugLog($"Failure while polling for mentions: {ex}");
            }
        }

        async Task ProcessCommentAsync(CommentInfo comment)
        {
            try
            {
                if (comment.Body.Contains('@'))
                {
                    await _gitHubNotifications.ProcessGitHubMentionAsync(comment);
                }

                if (comment.Body.Contains("@MihuBot", StringComparison.OrdinalIgnoreCase) &&
                    comment.User.Type == AccountType.User &&
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

                    if (!CheckGitHubUserPermissions(comment.User.Login))
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

                        if (comment.RepoOwner() == "dotnet" && comment.RepoName() == "runtime")
                        {
                            await ProcessMihuBotDotnetRuntimeMention(comment, arguments, pullRequest);
                        }
                        else if (comment.RepoOwner() == "dotnet" && comment.RepoName() == "yarp")
                        {
                            await ProcessMihuBotYarpMention(comment, arguments, pullRequest);
                        }
                    }
                    else
                    {
                        if (comment.RepoOwner() == "dotnet" && comment.RepoName() == "runtime")
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

        Task ProcessMihuBotDotnetRuntimeIssueMention(CommentInfo comment, string arguments)
        {
            if (BenchmarkWithCompareRangeRegex().Match(arguments) is { Success: true } benchmarkMatch)
            {
                StartBenchmarkJob(comment.User.Login, arguments, comment);
            }

            return Task.CompletedTask;
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

    public JobBase StartBackportJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment) =>
        StartJobCore(new BackportJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartCoreRootGenerationJob(string githubCommenterLogin, string arguments) =>
        StartJobCore(new CoreRootGenerationJob(this, githubCommenterLogin, arguments));

    public JobBase StartJobCore(JobBase job)
    {
        lock (_jobs)
        {
            if (_shuttingDown)
            {
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
            });
        }

        return job;
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

    public bool CheckGitHubUserPermissions(string userLogin) =>
        ConfigurationService.TryGet(null, $"RuntimeUtils.AuthorizedUser.{userLogin}", out string allowedString) &&
        bool.TryParse(allowedString, out bool allowed) &&
        allowed;

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
