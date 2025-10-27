using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using MihuBot.DB.Models;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class IssueTriageService(
    GitHubClient GitHub,
    IssueTriageHelper TriageHelper,
    GitHubDataIngestionService DataIngestion,
    Logger Logger,
    IDbContextFactory<GitHubDbContext> GitHubDb,
    ServiceConfiguration ServiceConfiguration)
    : IHostedService
{
    private static readonly SearchValues<string> s_issueBodiesToSkipOnUpdate = SearchValues.Create(
    [
        "<!-- Known issue validation start -->",
        "<!-- BEGIN: Github workflow runs test report -->",
        "Fill the error message using [step by step known issues guidance]",
    ], StringComparison.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _updateCts = new();
    private Task _updatesTask;

    private sealed record RepoConfig(string RepoName, Func<IQueryable<IssueInfo>, IQueryable<IssueInfo>> Filter, string FilterDescription);

    private static readonly RepoConfig[] s_repoConfigs =
    [
        new("dotnet/runtime",
            q => q.Where(i => i.Labels.Any(l => Constants.NetworkingLabels.Any(nl => nl == l.Name))),
            "All networking issues"),

        new("dotnet/aspire",
            q => q.Where(i => i.Labels.Any(l => l.Name == "area-dashboard")),
            "[`area-dashboard`](https://github.com/dotnet/aspire/issues?q=state%3Aopen%20label%3A%22area-dashboard%22) issues"),
    ];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            using AsyncFlowControl _ = ExecutionContext.SuppressFlow();

            _updatesTask = Task.Run(async () => await RunUpdateLoopAsync(cancellationToken), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _updateCts.CancelAsync();

        if (_updatesTask is not null)
        {
            await _updatesTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _updateCts.Token);
            cancellationToken = linkedCts.Token;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (ServiceConfiguration.PauseAutoTriage)
                    {
                        continue;
                    }

                    await TriageIssuesAsync(cancellationToken);

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    string errorMessage = $"{nameof(IssueTriageService)}: Update failed ({consecutiveFailureCount}): {ex}";
                    Logger.DebugLog(errorMessage);

                    await Task.Delay(TimeSpan.FromMinutes(5) * consecutiveFailureCount, cancellationToken);

                    if (consecutiveFailureCount == 2)
                    {
                        await Logger.DebugAsync(errorMessage);
                    }
                }
            }
        }
        catch when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected exception: {ex}");
        }
    }

    private async Task TriageIssuesAsync(CancellationToken cancellationToken)
    {
        foreach (RepoConfig repoConfig in s_repoConfigs)
        {
            await using GitHubDbContext db = GitHubDb.CreateDbContext();

            long repoId = await DataIngestion.TryGetKnownRepositoryIdAsync(repoConfig.RepoName, cancellationToken);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repoId, repoConfig.RepoName);

            IQueryable<IssueInfo> query = db.Issues
                .AsNoTracking()
                .OrderByDescending(i => i.UpdatedAt)
                .Where(issue =>
                    !db.TriagedIssues.Any(entry => entry.IssueId == issue.Id) ||
                    db.TriagedIssues.First(entry => entry.IssueId == issue.Id).UpdatedAt < issue.UpdatedAt)
                .Take(1000)
                .Where(i => i.RepositoryId == repoId)
                .Where(i => i.IssueType == IssueType.Issue);

            query = repoConfig.Filter(query);

            query = query.Take(100);

            query = IssueTriageHelper.AddIssueInfoIncludes(query);

            Stopwatch queryTimer = Stopwatch.StartNew();

            IssueInfo[] issues = await query
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken);

            queryTimer.Stop();

            Logger.TraceLog($"[{nameof(IssueTriageService)}] Query for {repoConfig.RepoName} took {queryTimer.ElapsedMilliseconds:F2} ms, got {issues.Length} issues.");

            int triaged = 0;

            foreach (IssueInfo issue in issues)
            {
                TriagedIssueRecord triagedIssue = await db.TriagedIssues.FirstOrDefaultAsync(i => i.IssueId == issue.Id, cancellationToken);

                if (triagedIssue is null)
                {
                    triagedIssue = new TriagedIssueRecord { IssueId = issue.Id };
                    db.TriagedIssues.Add(triagedIssue);
                }

                triagedIssue.UpdatedAt = DateTime.UtcNow;

                bool isNewIssue = triagedIssue.TriageReportIssueNumber == 0;

                if (DateTime.UtcNow - issue.CreatedAt <= TimeSpan.FromDays(7) &&
                    issue.State == ItemState.Open &&
                    issue.IssueType == IssueType.Issue &&
                    (isNewIssue || !issue.Body.ContainsAny(s_issueBodiesToSkipOnUpdate)) &&
                    !string.Equals(issue.Body, triagedIssue.Body, StringComparison.OrdinalIgnoreCase) &&
                    (isNewIssue || await db.BodyEditHistory.AsNoTracking().Where(e => e.ResourceIdentifier == issue.Id).CountAsync(cancellationToken) < 5))
                {
                    triagedIssue.Body = issue.Body;

                    await TriageIssueAsync(issue, triagedIssue, repoConfig.FilterDescription, cancellationToken);

                    triaged++;

                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);

            if (triaged > 0)
            {
                Logger.DebugLog($"[{nameof(IssueTriageService)}]: {triaged} issues triaged for {repoConfig.RepoName}.");
            }
        }
    }

    public async Task<Uri> ManualTriageAsync(IssueInfo issue, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = GitHubDb.CreateDbContext();

        TriagedIssueRecord triagedIssue = await db.TriagedIssues.FirstOrDefaultAsync(i => i.IssueId == issue.Id, cancellationToken);

        if (triagedIssue is null)
        {
            triagedIssue = new TriagedIssueRecord { IssueId = issue.Id };
            db.TriagedIssues.Add(triagedIssue);
        }

        triagedIssue.Body = issue.Body;

        await TriageIssueAsync(issue, triagedIssue, "Manual trigger", cancellationToken);

        await db.SaveChangesAsync(CancellationToken.None);

        return new Uri($"https://github.com/MihuBot/runtime-utils/issues/{triagedIssue.TriageReportIssueNumber}");
    }

    private async Task TriageIssueAsync(IssueInfo issue, TriagedIssueRecord triagedIssue, string repoFilterDescription, CancellationToken cancellationToken)
    {
        ConcurrentQueue<string> toolLogs = [];

        var options = new IssueTriageHelper.TriageOptions(TriageHelper.DefaultModel, "MihuBot", issue, toolLogs.Enqueue, SkipCommentsOnCurrentIssue: false);

        string html = await TriageHelper.TriageIssueAsync(options, cancellationToken).LastOrDefaultAsync(cancellationToken) ?? "";

        string commit = Helpers.Helpers.GetCommitId();
        string version = commit.Length >= 10 ? $"[`{commit.AsSpan(0, 6)}`](https://github.com/MihaZupan/MihuBot/tree/{commit})" : "unknown";

        string newIssueBody =
            $"""
            Triage for {issue.HtmlUrl}.
            Repo filter: {repoFilterDescription}.
            MihuBot version: {version}.
            Ping [MihaZupan](https://github.com/MihaZupan) for any issues.

            This is a test triage report generated by AI, aimed at helping the triage team quickly identify past issues/PRs that may be related.
            Take any conclusions with a large grain of salt.

            <details>
            <summary>Tool logs</summary>

            ```
            {string.Join('\n', toolLogs)}
            ```

            </details>

            {html}
            """;

        if (triagedIssue.TriageReportIssueNumber == 0)
        {
            string title = $"[✨ Triage] {issue.Repository.FullName}#{issue.Number} by {issue.User.Login} - {issue.Title}".TruncateWithDotDotDot(120);

            Issue newIssue = await GitHub.Issue.Create(JobBase.IssueRepositoryOwner, JobBase.IssueRepositoryName, new NewIssue(title)
            {
                Body = newIssueBody,
            });

            triagedIssue.TriageReportIssueNumber = newIssue.Number;
        }
        else
        {
            await GitHub.Issue.Update(JobBase.IssueRepositoryOwner, JobBase.IssueRepositoryName, triagedIssue.TriageReportIssueNumber, new IssueUpdate
            {
                Body = newIssueBody,
            });
        }
    }
}
