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
    // Repositories using dotnet/issue-labeler that overlap with /data-ingestion.
    // ASP.NET Core removed its ML labeler in dotnet/aspnetcore#69019.
    private static readonly string[] s_labelerRepositories =
        ["dotnet/runtime", "dotnet/extensions", "dotnet/roslyn", "dotnet/sdk"];

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
                .ThenInclude(r => r.Labels)
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
                AreaLabelSuggestion[] suggestions = await Detector.GetSuggestionsAsync(issue.Repository, issue, cancellationToken: cancellationToken);

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
        issues.Where(i => !i.Repository.Private && s_labelerRepositories.Contains(i.Repository.FullName))
            .Where(i => i.CreatedAt >= since)
            .Where(i => i.IssueType == IssueType.Issue || i.IssueType == IssueType.PullRequest)
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Number);

    internal static string FormatPrediction(IssueInfo issue, AreaLabelSuggestion[] suggestions)
    {
        string[] currentAreas = issue.Labels
            .Where(l => l.Name.StartsWith("area-", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Name)
            .ToArray();
        string currentLabels = string.Join(", ", currentAreas.Select(l => $"`{l}`"));
        string indicator = (currentAreas.Length > 0, suggestions.Length > 0) switch
        {
            (true, true) => AreaLabelHistory.Equal(currentAreas, suggestions.Select(s => s.LabelName)) ? "✅" : "❌",
            (false, false) => "⚪",
            _ => "➖",
        };

        bool isCopilotPr = issue.IssueType == IssueType.PullRequest && (issue.User?.Login.Contains("copilot", StringComparison.OrdinalIgnoreCase) ?? false);
        string itemReference = issue.Repository is { } repo ? $"{repo.FullName}#{issue.Number}" : $"#{issue.Number}";

        return
            $"""
            {indicator} [`{issue.Title.TruncateWithDotDotDot(100)}` - {(issue.IssueType == IssueType.PullRequest ? "PR " : "")}{itemReference}](<{issue.HtmlUrl}>){(isCopilotPr ? " (Copilot PR)" : "")}
            - Current: {(currentLabels.Length > 0 ? currentLabels : "<none>")}
            - Suggested: {(suggestions.Length > 0 ? string.Join(", ", suggestions.Select(s => $"`{s.LabelName}` ({s.Confidence:F2})")) : "<none>")}
            """;
    }

    internal static string GetProcessedKey(IssueInfo issue) => issue.HtmlUrl;

    internal static string NormalizeProcessedKey(string key) =>
        key.IndexOf("#updated-", StringComparison.Ordinal) is >= 0 and var index ? key[..index] : key;
}
