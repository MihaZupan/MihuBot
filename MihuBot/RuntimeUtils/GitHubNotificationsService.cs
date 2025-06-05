using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using Newtonsoft.Json;
using Octokit;
using Octokit.GraphQL.Internal;

namespace MihuBot.RuntimeUtils;

public sealed partial class GitHubNotificationsService
{
    public record UserRecord(string Name, string Token, DateTime PatUpdatedAt, string LastSubscribedIssue, int ErrorCount)
    {
        [JsonIgnore]
        public bool Disabled => ErrorCount >= 5;
    }

    private readonly FileBackedHashSet _processedMentions = new("ProcessedNotificationMentionIssues.txt", StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizedLocalJsonStore<Dictionary<string, UserRecord>> _users = new("GitHubNotificationUsers.json",
        init: (_, d) => new Dictionary<string, UserRecord>(d, StringComparer.OrdinalIgnoreCase));

    private readonly Logger Logger;
    public readonly GitHubClient Github;
    private readonly HttpClient Http;
    private readonly IConfigurationService Configuration;
    private readonly IDbContextFactory<GitHubDbContext> _db;

    public GitHubNotificationsService(Logger logger, GitHubClient github, HttpClient http, IConfigurationService configurationService, IDbContextFactory<GitHubDbContext> db)
    {
        Logger = logger;
        Github = github;
        Http = http;
        Configuration = configurationService;
        _db = db;

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(MonitorNetworkingIssuesWithoutNclMentionAsync);
        }
    }

    public async Task<bool> ProcessGitHubMentionAsync(CommentInfo comment)
    {
        bool enabledAny = false;

        try
        {
            if (Configuration.GetOrDefault(null, "RuntimeUtils.NclNotifications.Disable", false))
            {
                return enabledAny;
            }

            if (comment.RepoOwner() != "dotnet")
            {
                return enabledAny;
            }

            if (!TryDetectMentions(comment.Body.AsSpan(), out HashSet<UserRecord> users))
            {
                return enabledAny;
            }

            foreach (UserRecord user in users)
            {
                string duplicationKey = $"{comment.Issue.HtmlUrl}/{user.Name}";

                if (_processedMentions.Contains(duplicationKey))
                {
                    continue;
                }

                if (user.Disabled)
                {
                    Logger.DebugLog($"Skipping notifications on {comment.HtmlUrl} for {user.Name} due to previous errors.");
                    continue;
                }

                enabledAny = true;
                bool failed = false;

                try
                {
                    var connection = new Octokit.GraphQL.Connection(
                        new Octokit.GraphQL.ProductHeaderValue("MihuBot"),
                        new InMemoryCredentialStore(user.Token),
                        Http);

                    await connection.EnableIssueNotifiactionsAsync(comment.Issue.NodeIdentifier);

                    _processedMentions.TryAdd(duplicationKey);

                    Logger.DebugLog($"Enabled notifications on {comment.Issue.HtmlUrl} for {user.Name}");
                }
                catch (Exception ex)
                {
                    failed = true;
                    await Logger.DebugAsync($"Failed to enable notifications on {comment.HtmlUrl} for {user.Name}: {ex}");
                }
                finally
                {
                    var usersJson = await _users.EnterAsync();
                    try
                    {
                        if (usersJson.TryGetValue(user.Name, out var existingUser))
                        {
                            usersJson[user.Name] = existingUser with
                            {
                                LastSubscribedIssue = comment.Issue.HtmlUrl,
                                ErrorCount = failed ? existingUser.ErrorCount + 1 : 0,
                            };
                        }
                    }
                    finally
                    {
                        _users.Exit();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await Logger.DebugAsync($"Failed to enable notifications on {comment.HtmlUrl}: {ex}");
        }

        return enabledAny;
    }

    private bool TryDetectMentions(ReadOnlySpan<char> comment, out HashSet<UserRecord> users)
    {
        users = [];

        foreach (ValueMatch match in MentionsRegex().EnumerateMatches(comment))
        {
            string name = comment.Slice(match.Index, match.Length).TrimStart('@').ToString();

            if (name.Equals("dotnet/ncl", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string u in (ReadOnlySpan<string>)["MihaZupan", "CarnaViire", "karelz", "antonfirsov", "ManickaP", "wfurt", "rzikm", "liveans", "rokonec"])
                {
                    if (TryGetUser(u, out UserRecord nclUser))
                    {
                        users.Add(nclUser);
                    }
                }
            }

            if (TryGetUser(name, out UserRecord user))
            {
                users.Add(user);
            }
        }

        return users.Count != 0;
    }

    public bool TryGetUser(string name, out UserRecord user)
    {
        user = _users.Query(users => users.TryGetValue(name, out var user) ? user : null);
        return user is not null;
    }

    public async Task UpdatePATAsync(string name, string token)
    {
        Logger.DebugLog($"Updating PAT for {name}");

        var users = await _users.EnterAsync();
        try
        {
            users.TryGetValue(name, out UserRecord previous);

            users[name] = new UserRecord(name, token, DateTime.UtcNow, previous?.LastSubscribedIssue ?? "N/A", ErrorCount: 0);
        }
        finally
        {
            _users.Exit();
        }
    }

    private async Task MonitorNetworkingIssuesWithoutNclMentionAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    if (Configuration.GetOrDefault(null, $"{nameof(MonitorNetworkingIssuesWithoutNclMentionAsync)}.Pause", false))
                    {
                        continue;
                    }

                    await using GitHubDbContext db = _db.CreateDbContext();

                    DateTime start = DateTime.UtcNow - TimeSpan.FromDays(7);
                    DateTime end = DateTime.UtcNow - TimeSpan.FromMinutes(10);

                    IssueInfo[] networkingIssues = await db.Issues
                        .AsNoTracking()
                        .Where(i => i.CreatedAt >= start && i.CreatedAt <= end)
                        .Where(i => i.Labels.Any(l => Constants.NetworkingLabels.Any(nl => nl == l.Name)))
                        .Where(i => i.PullRequest == null)
                        .FromDotnetRuntime()
                        .Include(i => i.Comments)
                        .OrderByDescending(i => i.CreatedAt)
                        .Take(1000)
                        .AsSplitQuery()
                        .ToArrayAsync();

                    foreach (IssueInfo issue in networkingIssues)
                    {
                        if (issue.Comments.Any(c => c.Body.Contains("@dotnet/ncl", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        if (!_processedMentions.TryAdd($"NoNCLMention/{issue.HtmlUrl}"))
                        {
                            continue;
                        }

                        IReadOnlyList<IssueComment> updatedComments;
                        try
                        {
                            updatedComments = await Github.Issue.Comment.GetAllForIssue(issue.RepositoryId, issue.Number);
                        }
                        catch (Exception ex)
                        {
                            Logger.DebugLog($"Failed to get issue {issue.HtmlUrl}: {ex}");
                            continue;
                        }

                        if (updatedComments.Any(c => c.Body.Contains("@dotnet/ncl", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        Logger.DebugLog($"[{nameof(MonitorNetworkingIssuesWithoutNclMentionAsync)}]: No NCL mention in {issue.HtmlUrl}");

                        await Github.Issue.Comment.Create(issue.RepositoryId, issue.Number, "cc: @dotnet/ncl");
                    }

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    string errorMessage = $"{nameof(MonitorNetworkingIssuesWithoutNclMentionAsync)}: ({consecutiveFailureCount}): {ex}";
                    Logger.DebugLog(errorMessage);

                    await Task.Delay(TimeSpan.FromMinutes(5) * consecutiveFailureCount);

                    if (consecutiveFailureCount == 2)
                    {
                        await Logger.DebugAsync(errorMessage);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected exception: {ex}");
        }
    }

    [GeneratedRegex(@"@[\w\/]+")]
    private static partial Regex MentionsRegex();
}
