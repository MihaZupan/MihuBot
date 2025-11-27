using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils.AI;

public sealed class DetectIssueAreaLabelsService(
    Logger Logger,
    InitializedDiscordClient Discord,
    IDbContextFactory<GitHubDbContext> GitHubDb,
    ServiceConfiguration ServiceConfiguration,
    OpenAIService OpenAI)
    : BackgroundService
{
    private readonly FileBackedHashSet _processedIssues = new("ProcessedIssuesWithNeedsAreaLabel.txt", StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (ServiceConfiguration.PauseGitHubPolling)
                    {
                        continue;
                    }

                    await DoDetectionAsync(stoppingToken);

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    string errorMessage = $"{nameof(DetectIssueAreaLabelsService)}: ({consecutiveFailureCount}): {ex}";
                    Logger.DebugLog(errorMessage);

                    await Task.Delay(TimeSpan.FromMinutes(5) * consecutiveFailureCount, stoppingToken);

                    if (consecutiveFailureCount == 2)
                    {
                        await Logger.DebugAsync(errorMessage);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                Console.WriteLine($"Unexpected exception: {ex}");
            }
        }
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
                string issueData = (await IssueInfoForPrompt.CreateAsync(issue, GitHubDb, cancellationToken)).AsJson();

                ChatResponse<AreaLabelSuggestion[]> result = await OpenAI.GetChat("gpt-5-mini", secondary: true).GetResponseAsync<AreaLabelSuggestion[]>(
                    $"""
                    You are an expert at classifying GitHub issues related to .NET into different categories based on their content.
                    Your task is to determine which labels best match the new issue.

                    Choose from the following areas:
                    {string.Join(", ", repo.Labels
                        .Where(l => l.Name.StartsWith("area-", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(l => l.Name)
                        .Select(l => l.Name))}

                    Only return the labels which are likely relevant.
                    Include the confidence level between 0 and 1 (where 1 is absolute certainty).

                    Here is the issue data:
                    ```json
                    {issueData}
                    ```
                    """, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken);

                AreaLabelSuggestion[] suggestions = result.Result;

                if (suggestions is null)
                {
                    continue;
                }

                suggestions = [.. suggestions
                    .Where(s => s is not null && s.Confidence.HasValue && s.Confidence >= 0.5)
                    .OrderByDescending(s => s.Confidence)
                    .Take(5)];

                if (suggestions.Length == 0)
                {
                    continue;
                }

                await Discord.GetTextChannel(Channels.PrivateGeneral).TrySendMessageAsync(
                    $"Suggested labels for <{issue.HtmlUrl}>:\n{string.Join('\n', suggestions.Select(s => $"- {s.Confidence:F2} `{s.LabelName}`"))}");
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync($"Failed to do issue label detection for <{issue.HtmlUrl}>: {ex}");
            }
        }
    }

    private sealed record AreaLabelSuggestion(string LabelName, double? Confidence);
}
