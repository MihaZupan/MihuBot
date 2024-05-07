using Azure.Storage.Blobs;
using MihuBot.API;
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
        @MihuBot fuzz <fuzzer name>
        ```
        """;

    private readonly Dictionary<string, RuntimeUtilsJob> _jobs = new(StringComparer.Ordinal);
    private readonly FileBackedHashSet _processedMentions = new("ProcessedMentionComments.txt");

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
        await foreach (GitHubComment comment in Github.PollCommentsAsync(RepoOwner, RepoName, Logger))
        {
            try
            {
                if (comment.Url.Contains("/pull/", StringComparison.OrdinalIgnoreCase))
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
            if (comment.User.Type == AccountType.User &&
                comment.Body.Contains("@MihuBot", StringComparison.OrdinalIgnoreCase) &&
                _processedMentions.TryAdd(comment.CommentId.ToString()))
            {
                Logger.DebugLog($"Processing mention from {comment.User.Login} in {comment.Url}: '{comment.Body}'");

                int pullRequestNumber = int.Parse(new Uri(comment.Url, UriKind.Absolute).AbsolutePath.Split('/').Last());

                PullRequest pullRequest = await Github.PullRequest.Get(RepoOwner, RepoName, pullRequestNumber);

                if (pullRequest.State.Value == ItemState.Open)
                {
                    string arguments = comment.Body.AsSpan(comment.Body.IndexOf("@MihuBot", StringComparison.OrdinalIgnoreCase) + "@MihuBot".Length).Trim().ToString();

                    if (arguments.Contains("-help", StringComparison.OrdinalIgnoreCase) ||
                        arguments is "-h" or "-H" or "?" or "-?")
                    {
                        await Github.Issue.Comment.Create(RepoOwner, RepoName, pullRequestNumber, UsageCommentMarkdown);
                        return;
                    }

                    if (CheckGitHubUserPermissions(comment.User.Login))
                    {
                        var fuzzMatch = FuzzMatchRegex().Match(arguments);

                        if (fuzzMatch.Success)
                        {
                            string fuzzerName = fuzzMatch.Groups[1].Value;

                            if (fuzzerName.Length < 4)
                            {
                                await Github.Issue.Comment.Create(RepoOwner, RepoName, pullRequestNumber, "Please specify a fuzzer name. For example: `@MihuBot fuzz HttpHeadersFuzzer`");
                                return;
                            }

                            await StartFuzzJobAsync(pullRequest, fuzzerName);
                        }
                        else
                        {
                            StartJob(pullRequest, githubCommenterLogin: comment.User.Login, arguments);
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

    private async Task StartFuzzJobAsync(PullRequest pullRequest, string fuzzerName)
    {
        try
        {
            string repo = pullRequest.Head.Repository.FullName;
            string branch = pullRequest.Head.Ref;

            string scriptId = RunScriptController.AddScript(
                token =>
                    $$"""
                    git clone --progress https://github.com/dotnet/runtime runtime
                    cd runtime
                    git log -1
                    git config --global user.email build@build.foo
                    git config --global user.name build
                    git remote add pr https://github.com/{{repo}}
                    git fetch pr {{branch}}
                    git log pr/{{branch}} -1
                    git merge --no-edit pr/{{branch}}

                    ./build.cmd clr+libs+packs+host -rc Checked -c Debug

                    cd src/libraries/Fuzzing/DotnetFuzzing
                    ../../../../.dotnet/dotnet publish -o publish
                    ../../../../.dotnet/dotnet tool install --tool-path . SharpFuzz.CommandLine
                    publish/DotnetFuzzing.exe prepare-onefuzz deployment

                    deployment/{{fuzzerName}}/local-run.bat -timeout=60 -max_total_time=3600
                    """,
                TimeSpan.FromMinutes(5));

            await Github.Issue.Create(
                "MihuBot",
                "runtime-utils",
                new NewIssue($"[Fuzz {fuzzerName}] [{pullRequest.User.Login}] {pullRequest.Title}".TruncateWithDotDotDot(99))
                {
                    Body = $"run-win-{scriptId}-\n\n{pullRequest.HtmlUrl}"
                });
        }
        catch (Exception ex)
        {
            await Logger.DebugAsync($"Failed to start fuzz job for {pullRequest.HtmlUrl}: {ex}");
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

    [GeneratedRegex("^fuzz ?([a-z]*)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FuzzMatchRegex();
}
