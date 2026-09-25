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
    private readonly FileBackedHashSet _processedIssues = new(
        "ProcessedIssuesWithNeedsAreaLabel.txt", StringComparer.OrdinalIgnoreCase, NormalizeProcessedKey);

    protected override async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || ServiceConfiguration.PauseGitHubPolling || ServiceConfiguration.PauseAutoLabelPrediction)
        {
            return;
        }

        await MihuBotAIActivitySource.RunAutomaticAsync("AutomaticLabelPrediction",
            activity => DoDetectionAsync(activity, cancellationToken));
    }

    private async Task DoDetectionAsync(Activity activity, CancellationToken cancellationToken)
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

            if (ServiceConfiguration.PauseGitHubPolling || ServiceConfiguration.PauseAutoLabelPrediction)
            {
                activity?.SetTag("run.paused", true);
                return;
            }

            string processedKey = GetProcessedKey(issue);

            if (_processedIssues.Contains(processedKey))
            {
                continue;
            }

            using var issueActivity = MihuBotAIActivitySource.Instance.StartActivity("AutomaticLabelPredictionIssue");
            issueActivity?.SetOperation("triage", "automaticLabels");
            issueActivity?.SetIssueContext(issue);

            try
            {
                AreaLabelSuggestion[] suggestions = await Detector.GetSuggestionsAsync(repo, issue, cancellationToken: cancellationToken);

                var channel = Discord.GetTextChannel(Channels.SuggestedLabels)
                    ?? throw new InvalidOperationException("The suggested-labels channel is unavailable.");
                await channel.SendMessageAsync(FormatPrediction(issue, suggestions),
                    allowedMentions: AllowedMentions.None, options: new RequestOptions { CancelToken = cancellationToken });
                _processedIssues.TryAdd(processedKey);

                issueActivity?.SetSuccess();
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                issueActivity?.SetTag("run.cancelled", true);
                issueActivity?.SetError(ex);
                throw;
            }
            catch (Exception ex)
            {
                issueActivity?.SetError(ex);
                activity?.SetError(ex);

                await _logger.DebugAsync($"Failed to do label detection for <{issue.HtmlUrl}>: {ex}");
            }
        }
    }

    internal static IQueryable<IssueInfo> GetIncomingItems(IQueryable<IssueInfo> issues, DateTime since) =>
        issues.FromDotnetRuntime()
            .Where(i => i.CreatedAt >= since)
            .Where(i => i.IssueType == IssueType.Issue || i.IssueType == IssueType.PullRequest)
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Number);

    internal static string FormatPrediction(IssueInfo issue, AreaLabelSuggestion[] suggestions)
    {
        string currentLabels = string.Join(", ", issue.Labels
            .Where(l => l.Name.StartsWith("area-", StringComparison.OrdinalIgnoreCase))
            .Select(l => $"`{l.Name}`"));

        bool isCopilotPr = issue.IssueType == IssueType.PullRequest && (issue.User?.Login.Contains("copilot", StringComparison.OrdinalIgnoreCase) ?? false);

        return
            $"""
            [`{issue.Title.TruncateWithDotDotDot(100)}` - {(issue.IssueType == IssueType.PullRequest ? "PR " : "")}#{issue.Number}](<{issue.HtmlUrl}>){(isCopilotPr ? " (Copilot PR)" : "")}
            - Current: {(currentLabels.Length > 0 ? currentLabels : "<none>")}
            - Suggested: {(suggestions.Length > 0 ? string.Join(", ", suggestions.Select(s => $"`{s.LabelName}` ({s.Confidence:F2})")) : "<none>")}
            """;
    }

    internal static string GetProcessedKey(IssueInfo issue) => issue.HtmlUrl;

    internal static string NormalizeProcessedKey(string key) =>
        key.IndexOf("#updated-", StringComparison.Ordinal) is >= 0 and var index ? key[..index] : key;
}
