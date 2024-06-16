using Azure.Storage.Blobs;
using MihuBot.Configuration;
using Octokit;
using System.Text.RegularExpressions;

namespace MihuBot.RuntimeUtils;

public sealed partial class RuntimeUtilsService : IHostedService
{
    private const string RepoOwner = "dotnet";
    private const string RepoName = "runtime";

    private const string UsageCommentMarkdown =
        $"""
        See <https://mihubot.xyz/runtime-utils> for an alternative way of submitting jobs.

        ```
        Usage: @MihuBot [options]

        Options:
            -?|-help              Show help information

            -arm                  Get ARM64 diffs instead of X64.
            -nocctors             Avoid passing --cctors to jit-diff.
            -tier0                Generate tier0 code.
            -dependsOn <prs>      A comma-separated list of PR numbers to merge into the baseline branch.
            -combineWith <prs>    A comma-separated list of PR numbers to merge into the tested PR branch.

            -fast                 Run on a more powerful VM to save a few minutes.
            -hetzner              Run on a Hetzner VM instead of Azure.
            -intel                Run on an Intel-based VM instead of an AMD-based one.

            -includeKnownNoise    Display diffs affected by known noise (e.g. race conditions between different JIT runs).
            -includeNewMethodRegressions        Display diffs for new methods.
            -includeRemovedMethodImprovements   Display diffs for removed methods.
        ```

        Or
        ```
        @MihuBot fuzz <fuzzer name pattern>
        ```

        Or
        ```
        @MihuBot merge/rebase/format    Requires collaborator access on your fork
        ```
        """;

    private readonly Dictionary<string, JobBase> _jobs = new(StringComparer.Ordinal);
    private readonly FileBackedHashSet _processedMentions = new("ProcessedMentionComments.txt");

    public readonly Logger Logger;
    public readonly GitHubClient Github;
    public readonly HttpClient Http;
    public readonly IConfiguration Configuration;
    public readonly IConfigurationService ConfigurationService;
    public readonly HetznerClient Hetzner;
    public readonly BlobContainerClient ArtifactsBlobContainerClient;
    public readonly BlobContainerClient RunnerPersistentStateBlobContainerClient;

    public RuntimeUtilsService(Logger logger, GitHubClient github, HttpClient http, IConfiguration configuration, IConfigurationService configurationService, HetznerClient hetzner)
    {
        Logger = logger;
        Github = github;
        Http = http;
        Configuration = configuration;
        ConfigurationService = configurationService;
        Hetzner = hetzner;

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
        await foreach (GitHubComment comment in Github.PollCommentsAsync(RepoOwner, RepoName, Logger))
        {
            try
            {
                if (comment.Url.Contains("/pull/", StringComparison.OrdinalIgnoreCase) &&
                    comment.Body.Contains("@MihuBot", StringComparison.OrdinalIgnoreCase) &&
                    comment.User.Type == AccountType.User)
                {
                    await ProcessMihuBotMentions(comment);
                }

                if (comment.Body.Contains("@dotnet/ncl", StringComparison.OrdinalIgnoreCase) &&
                    _processedMentions.TryAdd($"{comment.RepoOwner}/{comment.RepoName}/{comment.IssueId}") &&
                    !ConfigurationService.TryGet(null, "RuntimeUtils.NclMentions.Disable", out _))
                {
                    ConfigurationService.TryGet(null, "RuntimeUtils.NclMentions.Text", out string mentions);

                    if (string.IsNullOrEmpty(mentions))
                    {
                        mentions = "@MihaZupan @CarnaViire @karelz @antonfirsov @ManickaP @wfurt @rzikm @liveans";
                    }

                    await Github.Issue.Comment.Create(RepoOwner, RepoName, comment.IssueId, mentions);
                }
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync($"Failure while processing comment {comment.Url} {comment.CommentId}: {ex}");
            }
        }

        async Task ProcessMihuBotMentions(GitHubComment comment)
        {
            if (_processedMentions.TryAdd(comment.CommentId.ToString()))
            {
                Logger.DebugLog($"Processing mention from {comment.User.Login} in {comment.Url}: '{comment.Body}'");

                int pullRequestNumber = int.Parse(new Uri(comment.Url, UriKind.Absolute).AbsolutePath.Split('/').Last());

                PullRequest pullRequest = await Github.PullRequest.Get(RepoOwner, RepoName, pullRequestNumber);

                if (pullRequest.State.Value == ItemState.Open)
                {
                    string arguments = comment.Body.AsSpan(comment.Body.IndexOf("@MihuBot", StringComparison.OrdinalIgnoreCase) + "@MihuBot".Length).Trim().ToString();

                    if (!comment.User.Login.Equals("MihuBot", StringComparison.OrdinalIgnoreCase) &&
                        (arguments.Contains("-help", StringComparison.OrdinalIgnoreCase) ||
                        arguments is "-h" or "-H" or "?" or "-?"))
                    {
                        await Github.Issue.Comment.Create(RepoOwner, RepoName, pullRequestNumber, UsageCommentMarkdown);
                        return;
                    }

                    if (CheckGitHubUserPermissions(comment.User.Login))
                    {
                        var fuzzMatch = FuzzMatchRegex().Match(arguments);

                        if (fuzzMatch.Success)
                        {
                            StartFuzzLibrariesJob(pullRequest, githubCommenterLogin: comment.User.Login, arguments);
                        }
                        else if (arguments.StartsWith("fuzz", StringComparison.OrdinalIgnoreCase))
                        {
                            await Github.Issue.Comment.Create(RepoOwner, RepoName, pullRequestNumber, "Usage: `@MihuBot fuzz <fuzzer name pattern>`");
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
                                StartRebaseJob(pullRequest, githubCommenterLogin: comment.User.Login, arguments);
                            }
                            else
                            {
                                await Github.Issue.Comment.Create(RepoOwner, RepoName, pullRequestNumber,
                                    $"""
                                    I don't have push access to your repository.
                                    You can add me as a collaborator at https://github.com/{pullRequest.Head.Repository.FullName}/settings/access
                                    """);

                                await Logger.DebugAsync($"User {comment.User.Login} requires collaborator access. <{pullRequest.HtmlUrl}>");
                            }
                        }
                        else
                        {
                            StartJitDiffJob(pullRequest, githubCommenterLogin: comment.User.Login, arguments);
                        }
                    }
                    else
                    {
                        if (!comment.User.Login.Equals("msftbot", StringComparison.OrdinalIgnoreCase) &&
                            !comment.User.Login.Equals("MihuBot", StringComparison.OrdinalIgnoreCase) &&
                            !comment.User.Login.Contains("dotnet-policy-service", StringComparison.OrdinalIgnoreCase))
                        {
                            await Logger.DebugAsync($"User {comment.User.Login} tried to start a job, but is not authorized. <{pullRequest.HtmlUrl}>");
                        }
                    }
                }
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

    public JobBase StartJitDiffJob(string repository, string branch, string githubCommenterLogin, string arguments)
    {
        var job = new JitDiffJob(this, repository, branch, githubCommenterLogin, arguments);
        StartJobCore(job);
        return job;
    }

    public JobBase StartJitDiffJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment = null)
    {
        var job = new JitDiffJob(this, pullRequest, githubCommenterLogin, arguments, comment);
        StartJobCore(job);
        return job;
    }

    public JobBase StartFuzzLibrariesJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment = null)
    {
        var job = new FuzzLibrariesJob(this, pullRequest, githubCommenterLogin, arguments, comment);
        StartJobCore(job);
        return job;
    }

    public JobBase StartRebaseJob(PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment = null)
    {
        var job = new RebaseJob(this, pullRequest, githubCommenterLogin, arguments, comment);
        StartJobCore(job);
        return job;
    }

    private void StartJobCore(JobBase job)
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
                await Task.Delay(TimeSpan.FromDays(7));

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
    }

    public JobBase[] GetAllActiveJobs() => _jobs
        .Where(pair => pair.Key == pair.Value.ExternalId)
        .Select(pair => pair.Value)
        .Where(job => !job.Completed)
        .OrderByDescending(job => job.Stopwatch.Elapsed)
        .ToArray();

    public bool CheckGitHubUserPermissions(string userLogin) =>
        ConfigurationService.TryGet(null, $"RuntimeUtils.AuthorizedUser.{userLogin}", out string allowedString) &&
        bool.TryParse(allowedString, out bool allowed) &&
        allowed;

    public async Task<PullRequest> GetPullRequestAsync(int prNumber)
    {
        PullRequest pullRequest = await Github.PullRequest.Get(RepoOwner, RepoName, prNumber);
        ArgumentNullException.ThrowIfNull(pullRequest);
        return pullRequest;
    }

    [GeneratedRegex(@"^fuzz ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FuzzMatchRegex();
}
