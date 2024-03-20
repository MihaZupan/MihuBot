using Azure.Storage.Blobs;
using MihuBot.Configuration;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class RuntimeUtilsService : IHostedService
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
            -aci                  Run on Azure Container Instances instead of a VM (slower, does not support -arm, -intel).
            -intel                Run on an Intel-based VM instead of an AMD-based one.

            -includeKnownNoise    Display diffs affected by known noise (e.g. race conditions between different JIT runs).
            -includeNewMethodRegressions        Display diffs for new methods.
            -includeRemovedMethodImprovements   Display diffs for removed methods.
        ```
        """;

    private readonly Dictionary<string, RuntimeUtilsJob> _jobs = new(StringComparer.Ordinal);

    public readonly Logger Logger;
    public readonly GitHubClient Github;
    public readonly HttpClient Http;
    public readonly IConfiguration Configuration;
    public readonly IConfigurationService ConfigurationService;
    public readonly HetznerClient Hetzner;
    public readonly BlobContainerClient ArtifactsBlobContainerClient;
    public readonly BlobContainerClient RunnerBaselineBlobContainerClient;

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

            RunnerBaselineBlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString-RuntimeUtils"],
                "runner-baseline");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(WatchForGitHubMentionsAsync);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WatchForGitHubMentionsAsync()
    {
        List<(int Id, Stopwatch Timestamp)> processedMentions = new();

        DateTimeOffset lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
        DateTimeOffset lastCheckTimeIssueComments = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                processedMentions.RemoveAll(c => c.Timestamp.Elapsed.TotalDays > 14);

                IReadOnlyList<PullRequestReviewComment> pullReviewComments = await Github.PullRequest.ReviewComment.GetAllForRepository(RepoOwner, RepoName, new PullRequestReviewCommentRequest
                {
                    Since = lastCheckTimeReviewComments
                }, new ApiOptions { PageCount = 100 });

                if (pullReviewComments.Count > 0)
                {
                    lastCheckTimeReviewComments = pullReviewComments.Max(c => c.CreatedAt);
                }

                foreach (PullRequestReviewComment reviewComment in pullReviewComments)
                {
                    await Process(reviewComment.Id, reviewComment.PullRequestUrl, reviewComment.Body, reviewComment.User);
                }

                IReadOnlyList<IssueComment> issueComments = await Github.Issue.Comment.GetAllForRepository(RepoOwner, RepoName, new IssueCommentRequest
                {
                    Since = lastCheckTimeIssueComments,
                    Sort = IssueCommentSort.Created,
                }, new ApiOptions { PageCount = 100 });

                if (issueComments.Count > 0)
                {
                    lastCheckTimeIssueComments = issueComments.Max(c => c.CreatedAt);
                }

                var recentTimestamp = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);

                if (recentTimestamp > lastCheckTimeIssueComments)
                {
                    lastCheckTimeIssueComments = recentTimestamp;
                }

                if (recentTimestamp > lastCheckTimeReviewComments)
                {
                    lastCheckTimeReviewComments = recentTimestamp;
                }

                foreach (IssueComment issueComment in issueComments)
                {
                    if (issueComment.HtmlUrl.Contains("/pull/", StringComparison.OrdinalIgnoreCase))
                    {
                        await Process(issueComment.Id, issueComment.HtmlUrl, issueComment.Body, issueComment.User);
                    }
                }

                async Task Process(int commentId, string pullRequestUrl, string body, User user)
                {
                    if (user.Type == AccountType.User &&
                        body.Contains("@MihuBot", StringComparison.OrdinalIgnoreCase) &&
                        !processedMentions.Any(c => c.Id == commentId))
                    {
                        Logger.DebugLog($"Processing mention from {user.Login} in {pullRequestUrl}: '{body}'");

                        processedMentions.Add((commentId, Stopwatch.StartNew()));

                        int pullRequestNumber = int.Parse(new Uri(pullRequestUrl, UriKind.Absolute).AbsolutePath.Split('/').Last());

                        PullRequest pullRequest = await Github.PullRequest.Get(RepoOwner, RepoName, pullRequestNumber);

                        if (pullRequest.State.Value == ItemState.Open)
                        {
                            if (CheckGitHubUserPermissions(user.Login))
                            {
                                string arguments = body.AsSpan(body.IndexOf("@MihuBot", StringComparison.OrdinalIgnoreCase) + "@MihuBot".Length).Trim().ToString();

                                if (arguments.Contains("-help", StringComparison.OrdinalIgnoreCase) ||
                                    arguments is "-h" or "-H" or "?" or "-?")
                                {
                                    await Github.Issue.Comment.Create(RepoOwner, RepoName, pullRequestNumber, UsageCommentMarkdown);
                                    return;
                                }

                                StartJob(pullRequest, githubCommenterLogin: user.Login, arguments);
                            }
                            else
                            {
                                if (!user.Login.Equals("msftbot", StringComparison.OrdinalIgnoreCase) &&
                                    !user.Login.Equals("MihuBot", StringComparison.OrdinalIgnoreCase))
                                {
                                    await Logger.DebugAsync($"User {user.Login} tried to start a job, but is not authorized. <{pullRequest.HtmlUrl}>");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
                lastCheckTimeIssueComments = DateTime.UtcNow;
                Logger.DebugLog($"Failed to fetch GitHub notifications: {ex}");
            }
        }
    }

    public bool TryGetJob(string jobId, bool publicId, out RuntimeUtilsJob job)
    {
        lock (_jobs)
        {
            return _jobs.TryGetValue(jobId, out job) &&
                (publicId ? job.ExternalId : job.JobId) == jobId;
        }
    }

    public RuntimeUtilsJob StartJob(string repository, string branch, string githubCommenterLogin, string arguments)
    {
        var job = new RuntimeUtilsJob(this, repository, branch, githubCommenterLogin, arguments);
        StartJobCore(job);
        return job;
    }

    public RuntimeUtilsJob StartJob(PullRequest pullRequest, string githubCommenterLogin = null, string arguments = null)
    {
        var job = new RuntimeUtilsJob(this, pullRequest, githubCommenterLogin, arguments);
        StartJobCore(job);
        return job;
    }

    private void StartJobCore(RuntimeUtilsJob job)
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

    public RuntimeUtilsJob[] GetAllActiveJobs() => _jobs
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
}
