using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.Search;
using Octokit;

namespace MihuBot.RuntimeUtils.AI;

public sealed class DetectIssueAreaLabelsService(
    Logger Logger,
    InitializedDiscordClient Discord,
    IDbContextFactory<GitHubDbContext> GitHubDb,
    ServiceConfiguration ServiceConfiguration,
    OpenAIService OpenAI,
    GitHubSearchService Search)
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
                AreaLabelSuggestion[] suggestions = await GetAreaLabelSuggestionsAsync(repo, issue, cancellationToken);

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

    private async Task<AreaLabelSuggestion[]> GetAreaLabelSuggestionsAsync(RepositoryInfo repo, IssueInfo issue, CancellationToken cancellationToken)
    {
        string issueData = (await IssueInfoForPrompt.CreateAsync(issue, GitHubDb, cancellationToken)).AsJson();

        (IssueInfo Issue, double Score)[] similarIssues = await GetSimilarIssueAsync(issue);

        var similarIssuesData = similarIssues
            .Select(si =>
            {
                string areaLabel = si.Issue.Labels.FirstOrDefault(l => l.Name.StartsWith("area-", StringComparison.OrdinalIgnoreCase))?.Name;
                if (areaLabel is null)
                {
                    return null;
                }

                return new
                {
                    Title = si.Issue.Title.TruncateWithDotDotDot(100),
                    Body = si.Issue.Body.TruncateWithDotDotDot(1000),
                    AreaLabel = areaLabel,
                };
            })
            .Where(d => d is not null)
            .ToArray();

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

            Here are some issues that may be similar to this one, and the labels they were assigned:
            ```json
            {JsonSerializer.Serialize(similarIssuesData)}
            ```
            """, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken);

        AreaLabelSuggestion[] suggestions = result.Result;

        if (suggestions is null)
        {
            return [];
        }

        suggestions = [.. suggestions
            .Where(s => s is not null && s.Confidence.HasValue && s.Confidence >= 0.5)
            .OrderByDescending(s => s.Confidence)
            .Take(5)];

        return suggestions;
    }

    private async Task<(IssueInfo Issue, double Score)[]> GetSimilarIssueAsync(IssueInfo issue)
    {
        string titleInfo = $"{issue.Repository.FullName}#{issue.Number}: {issue.Title}";
        string description = $"{titleInfo}\n{issue.IssueType.ToDisplayString()} author: {issue.User.Login}\n\n{issue.Body?.Trim()}";

        description = SemanticMarkdownChunker.TrimTextToTokens(Search.Tokenizer, description, SemanticMarkdownChunker.MaxSectionTokens);

        var searchResults = await Search.SearchIssuesAndCommentsAsync(
            description,
            new IssueSearchFilters { Repository = issue.Repository.FullName },
            new IssueSearchResponseOptions { MaxResults = 10, IncludeIssueComments = false },
            cancellationToken: CancellationToken.None);

        return searchResults.Results
            .Where(r => r.Score >= 0.3 && r.Results[0].Issue.Id != issue.Id)
            .Select(r => (r.Results[0].Issue, r.Score))
            .ToArray();
    }

    private sealed record AreaLabelSuggestion(string LabelName, double? Confidence);
}
