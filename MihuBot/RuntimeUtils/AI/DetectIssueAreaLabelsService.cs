using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.Discord;
using Octokit;

namespace MihuBot.RuntimeUtils.AI;

public sealed class DetectIssueAreaLabelsService(
    Logger Logger,
    InitializedDiscordClient Discord,
    IDbContextFactory<GitHubDbContext> GitHubDb,
    ServiceConfiguration ServiceConfiguration,
    AreaLabelDetector Detector)
    : PeriodicBackgroundService(new PeriodicTaskOptions { Interval = TimeSpan.FromMinutes(1) }, Logger)
{
    private readonly Logger _logger = Logger;
    private readonly FileBackedHashSet _processedIssues = new("ProcessedIssuesWithNeedsAreaLabel.txt", StringComparer.OrdinalIgnoreCase);

    protected override async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || ServiceConfiguration.PauseGitHubPolling)
        {
            return;
        }

        await DoDetectionAsync(cancellationToken);
    }

    private async Task DoDetectionAsync(CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = GitHubDb.CreateDbContext();

        DateTime onlyRecentlyUpdated = DateTime.UtcNow - TimeSpan.FromDays(1);

        IssueInfo[] unlabeledIssues = await db.Issues
            .AsNoTracking()
            .Where(i => i.UpdatedAt >= onlyRecentlyUpdated)
            .Where(i => i.Labels.Any(l => l.Name == "needs-area-label"))
            .Where(i => i.IssueType == IssueType.Issue)
            .Where(i => i.State == ItemState.Open)
            .FromDotnetRuntime()
            .Include(i => i.Repository)
            .Include(i => i.User)
            .Include(i => i.Comments)
                .ThenInclude(c => c.User)
            .Include(i => i.Labels)
            .OrderByDescending(i => i.CreatedAt)
            .Take(100)
            .AsSplitQuery()
            .ToArrayAsync(cancellationToken);

        if (unlabeledIssues.Length == 0)
        {
            return;
        }

        RepositoryInfo repo = await db.Repositories
            .AsNoTracking()
            .OnlyDotnetRuntime()
            .Include(r => r.Labels)
            .AsSplitQuery()
            .SingleAsync(cancellationToken);

        foreach (IssueInfo issue in unlabeledIssues)
        {
            if (issue.Labels.Any(l => l.Name.StartsWith("area-", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!_processedIssues.TryAdd(issue.HtmlUrl))
            {
                continue;
            }

            try
            {
                AreaLabelSuggestion[] suggestions = await Detector.GetSuggestionsAsync(repo, issue, cancellationToken);

                if (suggestions.Length == 0)
                {
                    continue;
                }

                await Discord.GetTextChannel(Channels.SuggestedLabels).TrySendMessageAsync(
                    $"Suggested labels for <{issue.HtmlUrl}>:\n{string.Join('\n', suggestions.Select(s => $"- {s.Confidence:F2} `{s.LabelName}`"))}");
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync($"Failed to do issue label detection for <{issue.HtmlUrl}>: {ex}");
            }
        }
    }
}
