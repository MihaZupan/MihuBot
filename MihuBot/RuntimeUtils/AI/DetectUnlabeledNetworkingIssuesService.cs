using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;

namespace MihuBot.RuntimeUtils.AI;

public sealed class DetectUnlabeledNetworkingIssuesService(
    Logger Logger,
    InitializedDiscordClient Discord,
    IDbContextFactory<GitHubDbContext> GitHubDb,
    ServiceConfiguration ServiceConfiguration,
    OpenAIService OpenAI)
    : BackgroundService
{
    private readonly FileBackedHashSet _processedIssues = new("ProcessedUnlabeledIssuesCheckingForNetworkingContent.txt", StringComparer.OrdinalIgnoreCase);

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

                    string errorMessage = $"{nameof(DetectUnlabeledNetworkingIssuesService)}: ({consecutiveFailureCount}): {ex}";
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
            .FromDotnetRuntime()
            .Include(i => i.User)
            .Include(i => i.Comments)
                .ThenInclude(c => c.User)
            .Include(i => i.Labels)
            .OrderByDescending(i => i.CreatedAt)
            .Take(100)
            .AsSplitQuery()
            .ToArrayAsync(cancellationToken);

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

                ChatResponse<bool> result = await OpenAI.GetChat("gpt-5-mini", secondary: true).GetResponseAsync<bool>(
                    $"""
                    You are an expert at classifying GitHub issues related to .NET into different categories based on their content.
                    Your task is to determine whether the following GitHub issue is related to networking problems and should be classified under any of the following labels:
                    {string.Join(", ", Constants.NetworkingLabels)}

                    Look for topics such as:
                    - network connectivity issues,
                    - HTTP/HTTPS problems,
                    - DNS resolution failures,
                    - socket programming errors,
                    - firewall or proxy issues,
                    - latency or performance problems related to networking,
                    - any other issues that directly involve network communication or protocols.

                    .NET types that ore often relevant include Uri, HttpClient, Socket, SslStream, Quic, HttpClientFactory, Kestrel, etc.

                    Here is the issue data:
                    ```json
                    {issueData}
                    ```

                    Respond with true if the issue is related to networking problems and should be labeled accordingly.
                    """, cancellationToken: cancellationToken);

                if (!result.Result)
                {
                    continue;
                }

                await Discord.GetTextChannel(Channels.PrivateGeneral).TrySendMessageAsync($"Possible networking issue: <{issue.HtmlUrl}>");
            }
            catch (Exception ex)
            {
                await Logger.DebugAsync($"Failed to do networking content discovery for <{issue.HtmlUrl}>: {ex}");
            }
        }
    }
}
