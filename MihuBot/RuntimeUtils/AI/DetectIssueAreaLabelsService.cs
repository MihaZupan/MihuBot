using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.Discord;

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

        IssueInfo[] incomingItems = await GetIncomingItems(db.Issues, DateTime.UtcNow - TimeSpan.FromDays(1))
            .AsNoTracking()
            .Include(i => i.Repository)
            .Include(i => i.User)
            .Include(i => i.Comments)
                .ThenInclude(c => c.User)
            .Include(i => i.Labels)
            .AsSplitQuery()
            .ToArrayAsync(cancellationToken);

        if (incomingItems.Length == 0)
        {
            return;
        }

        RepositoryInfo repo = await db.Repositories
            .AsNoTracking()
            .OnlyDotnetRuntime()
            .Include(r => r.Labels)
            .AsSplitQuery()
            .SingleAsync(cancellationToken);

        foreach (IssueInfo issue in incomingItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string processedKey = GetProcessedKey(issue);

            if (_processedIssues.Contains(processedKey))
            {
                continue;
            }

            try
            {
                AreaLabelSuggestion[] suggestions = await Detector.GetSuggestionsAsync(repo, issue, cancellationToken: cancellationToken);

                var channel = Discord.GetTextChannel(Channels.SuggestedLabels)
                    ?? throw new InvalidOperationException("The suggested-labels channel is unavailable.");
                await channel.SendMessageAsync(FormatPrediction(issue, suggestions),
                    allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
                _processedIssues.TryAdd(processedKey);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync($"Failed to do label detection for <{issue.HtmlUrl}>: {ex}");
            }
        }
    }

    internal static IQueryable<IssueInfo> GetIncomingItems(IQueryable<IssueInfo> issues, DateTime since) =>
        issues.FromDotnetRuntime()
            .Where(i => i.CreatedAt >= since || i.UpdatedAt >= since)
            .Where(i => i.IssueType == IssueType.Issue || i.IssueType == IssueType.PullRequest)
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Number);

    internal static string FormatPrediction(IssueInfo issue, AreaLabelSuggestion[] suggestions) => suggestions.Length == 0
        ? $"No confident area-label prediction for <{issue.HtmlUrl}>."
        : $"Suggested labels for <{issue.HtmlUrl}>:\n{string.Join('\n', suggestions.Select(s => $"- {s.Confidence:F2} `{s.LabelName}`"))}";

    internal static string GetProcessedKey(IssueInfo issue) => issue.IssueType == IssueType.PullRequest
        ? $"{issue.HtmlUrl}#updated-{issue.UpdatedAt.Ticks}"
        : issue.HtmlUrl;
}
