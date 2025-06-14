﻿using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class IssueTriageService(GitHubClient GitHub, IssueTriageHelper TriageHelper, Logger Logger, IDbContextFactory<GitHubDbContext> GitHubDb, IConfigurationService Configuration) : IHostedService
{
    private readonly CancellationTokenSource _updateCts = new();
    private Task _updatesTask;

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
                    if (Configuration.GetOrDefault(null, $"{nameof(IssueTriageService)}.Pause", false))
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
        using GitHubDbContext db = GitHubDb.CreateDbContext();

        string[] issueIds = await db.Issues
            .AsNoTracking()
            .Where(issue =>
                !db.TriagedIssues.Any(entry => entry.IssueId == issue.Id) ||
                db.TriagedIssues.First(entry => entry.IssueId == issue.Id).UpdatedAt < issue.UpdatedAt)
            .OrderBy(i => i.UpdatedAt)
            .Where(i => i.Labels.Any(l => Constants.NetworkingLabels.Any(nl => nl == l.Name)))
            .FromDotnetRuntime()
            .Select(i => i.Id)
            .Take(50)
            .ToArrayAsync(cancellationToken);

        int triaged = 0;

        foreach (string issueId in issueIds)
        {
            IssueInfo issue = await TriageHelper.GetIssueAsync(issues => issues.Where(i => i.Id == issueId), cancellationToken);
            TriagedIssueRecord triagedIssue = await db.TriagedIssues.FirstOrDefaultAsync(i => i.IssueId == issueId, cancellationToken);

            if (triagedIssue is null)
            {
                triagedIssue = new TriagedIssueRecord { IssueId = issueId };
                db.TriagedIssues.Add(triagedIssue);
            }

            triagedIssue.UpdatedAt = DateTime.UtcNow;

            if (issue.CreatedAt >= new DateTime(2025, 06, 10, 20, 00, 00, DateTimeKind.Utc) &&
                issue.State == ItemState.Open &&
                issue.PullRequest is null &&
                !issue.Body.Contains("<!-- Known issue validation start -->", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(issue.Body, triagedIssue.Body, StringComparison.OrdinalIgnoreCase))
            {
                triagedIssue.Body = issue.Body;

                await TriageIssueAsync(issue, triagedIssue, cancellationToken);

                triaged++;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }

        if (triaged > 0)
        {
            Logger.DebugLog($"[{nameof(IssueTriageService)}]: {issueIds.Length} issues triaged.");
        }
    }

    private async Task TriageIssueAsync(IssueInfo issue, TriagedIssueRecord triagedIssue, CancellationToken cancellationToken)
    {
        ConcurrentQueue<string> toolLogs = [];

        string html = await TriageHelper.TriageIssueAsync(TriageHelper.DefaultModel, "MihuBot", issue, toolLogs.Enqueue, cancellationToken)
            .LastOrDefaultAsync(cancellationToken) ?? "";

        string newIssueBody =
            $"""
            Triage for {issue.HtmlUrl}.
            Last updated: {DateTime.UtcNow.ToISODateTime()}

            ```
            {string.Join('\n', toolLogs)}
            ```

            {html}
            """;

        if (triagedIssue.TriageReportIssueNumber == 0)
        {
            string title = $"Triage for {issue.RepoOwner()}/{issue.RepoName()}#{issue.Number} by {issue.User.Login} - {issue.Title.TruncateWithDotDotDot(100)}";

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
