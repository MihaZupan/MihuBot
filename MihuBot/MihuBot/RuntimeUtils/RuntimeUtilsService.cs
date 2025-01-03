using Azure.Storage.Blobs;
using Markdig;
using Markdig.Syntax;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
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
            <link to a custom dotnet/performance branch>
            -medium/long

        Example:
            @MihuBot benchmark Regex
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
    public readonly UrlShortenerService UrlShortener;
    private readonly IDbContextFactory<MihuBotDbContext> _db;

    public RuntimeUtilsService(Logger logger, GitHubClient github, GitHubNotificationsService gitHubNotifications, HttpClient http, IConfiguration configuration, IConfigurationService configurationService, HetznerClient hetzner, IDbContextFactory<MihuBotDbContext> db, UrlShortenerService urlShortener)
    {
        Logger = logger;
        Github = github;
        _gitHubNotifications = gitHubNotifications;
        Http = http;
        Configuration = configuration;
        ConfigurationService = configurationService;
        Hetzner = hetzner;
        _db = db;
        UrlShortener = urlShortener;

        if (Program.AzureEnabled)
        {
            ArtifactsBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "artifacts");

            RunnerPersistentStateBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "runner-persistent");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(WatchForGitHubMentionsAsync, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WatchForGitHubMentionsAsync()
    {
        await Task.WhenAll(
            Task.Run(async () =>
            {
                await foreach (GitHubComment comment in Github.PollCommentsAsync("dotnet", "runtime", TimeSpan.FromSeconds(15), Logger))
                {
                    await ProcessCommentAsync(comment);
                }
            }),
            Task.Run(async () =>
            {
                await foreach (GitHubComment comment in Github.PollCommentsAsync("microsoft", "reverse-proxy", TimeSpan.FromSeconds(30), Logger))
                {
                    await ProcessCommentAsync(comment);
                }
            }));

        async Task ProcessCommentAsync(GitHubComment comment)
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
                    _processedMentions.TryAdd(comment.CommentId.ToString()) &&
                    TryExtractMihuBotArguments(comment.Body, out string arguments))
                {
                    Logger.DebugLog($"Processing mention from {comment.User.Login} in {comment.Url}: '{comment.Body}'");

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
                                User {comment.User.Login} tried to start a job, but is not authorized. <{comment.Url}>

                                `!cfg set global RuntimeUtils.AuthorizedUser.{comment.User.Login} true`
                                """);
                        }
                        return;
                    }

                    if (comment.IsOnPullRequest)
                    {
                        int pullRequestNumber = int.Parse(new Uri(comment.Url, UriKind.Absolute).AbsolutePath.Split('/').Last());
                        PullRequest pullRequest = await Github.PullRequest.Get(comment.RepoOwner, comment.RepoName, pullRequestNumber);

                        if (comment.RepoOwner == "dotnet" && comment.RepoName == "runtime")
                        {
                            await ProcessMihuBotDotnetRuntimeMentions(comment, arguments, pullRequest);
                        }
                        else if (comment.RepoOwner == "microsoft" && comment.RepoName == "reverse-proxy")
                        {
                            await ProcessMihuBotReverseProxyMentions(comment, arguments, pullRequest);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync($"Failure while processing comment {comment.Url} {comment.CommentId}: {ex}");
            }
        }

        Task ProcessMihuBotReverseProxyMentions(GitHubComment comment, string arguments, PullRequest pullRequest)
        {
            if (arguments.StartsWith("backport to ", StringComparison.OrdinalIgnoreCase))
            {
                StartBackportJob(pullRequest, comment.User.Login, arguments, comment);
            }

            return Task.CompletedTask;
        }

        async Task ProcessMihuBotDotnetRuntimeMentions(GitHubComment comment, string arguments, PullRequest pullRequest)
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

        async Task ReplyToCommentAsync(GitHubComment comment, string content)
        {
            await Github.Issue.Comment.Create(comment.RepoOwner, comment.RepoName, comment.IssueId, content);
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

    public JobBase StartJitDiffJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment) =>
        StartJobCore(new JitDiffJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartFuzzLibrariesJob(BranchReference branch, string githubCommenterLogin, string arguments) =>
        StartJobCore(new FuzzLibrariesJob(this, branch, githubCommenterLogin, arguments));

    public JobBase StartFuzzLibrariesJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment) =>
        StartJobCore(new FuzzLibrariesJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartRebaseJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment) =>
        StartJobCore(new RebaseJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartBenchmarkJob(BranchReference branch, string githubCommenterLogin, string arguments) =>
        StartJobCore(new BenchmarkLibrariesJob(this, branch, githubCommenterLogin, arguments));

    public JobBase StartBenchmarkJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment) =>
        StartJobCore(new BenchmarkLibrariesJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartRegexDiffJob(BranchReference branch, string githubCommenterLogin, string arguments) =>
        StartJobCore(new RegexDiffJob(this, branch, githubCommenterLogin, arguments));

    public JobBase StartRegexDiffJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment) =>
        StartJobCore(new RegexDiffJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    public JobBase StartBackportJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment) =>
        StartJobCore(new BackportJob(this, pullRequest, githubCommenterLogin, arguments, comment));

    private JobBase StartJobCore(JobBase job)
    {
        lock (_jobs)
        {
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
        await using var context = _db.CreateDbContext();

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
        await using var context = _db.CreateDbContext();

        context.CompletedJobs.Add(record.ToDbEntry());

        await context.SaveChangesAsync();
    }

    [GeneratedRegex(@"^fuzz ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FuzzMatchRegex();

    [GeneratedRegex(@"^benchmark ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BenchmarkFilterNameRegex();
}
