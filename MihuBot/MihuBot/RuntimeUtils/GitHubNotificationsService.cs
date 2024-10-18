using MihuBot.Configuration;
using Octokit;
using Octokit.GraphQL.Internal;
using System.Text.RegularExpressions;

namespace MihuBot.RuntimeUtils;

public sealed partial class GitHubNotificationsService
{
    public record UserRecord(string Name, string Token, DateTime PatUpdatedAt, string LastSubscribedIssue);

    private readonly FileBackedHashSet _processedMentions = new("ProcessedNotificationMentionIssues.txt", StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizedLocalJsonStore<Dictionary<string, UserRecord>> _users = new("GitHubNotificationUsers.json",
        init: (_, d) => new Dictionary<string, UserRecord>(d, StringComparer.OrdinalIgnoreCase));

    private readonly Logger Logger;
    public readonly GitHubClient Github;
    private readonly HttpClient Http;
    private readonly IConfigurationService ConfigurationService;

    public GitHubNotificationsService(Logger logger, GitHubClient github, HttpClient http, IConfigurationService configurationService)
    {
        Logger = logger;
        Github = github;
        Http = http;
        ConfigurationService = configurationService;
    }

    public async Task<bool> ProcessGitHubMentionAsync(GitHubComment comment)
    {
        bool enabledAny = false;

        try
        {
            Logger.DebugLog($"Processing mentions comment {comment.Url}: '{comment.Body}'");

            if (ConfigurationService.TryGet(null, "RuntimeUtils.NclNotifications.Disable", out _))
            {
                return enabledAny;
            }

            if (!TryDetectMentions(comment.Body.AsSpan(), out HashSet<UserRecord> users))
            {
                return enabledAny;
            }

            Issue issue = await Github.Issue.Get(comment.RepoOwner, comment.RepoName, comment.IssueId);

            foreach (UserRecord user in users)
            {
                if (!_processedMentions.TryAdd($"{issue.HtmlUrl}/{user.Name}"))
                {
                    continue;
                }

                enabledAny = true;

                try
                {
                    var connection = new Octokit.GraphQL.Connection(
                        new Octokit.GraphQL.ProductHeaderValue("MihuBot"),
                        new InMemoryCredentialStore(user.Token),
                        Http);

                    await connection.EnableIssueNotifiactionsAsync(issue);

                    var usersJson = await _users.EnterAsync();
                    try
                    {
                        if (usersJson.TryGetValue(user.Name, out var existingUser))
                        {
                            usersJson[user.Name] = existingUser with { LastSubscribedIssue = issue.HtmlUrl };
                        }
                    }
                    finally
                    {
                        _users.Exit();
                    }

                    Logger.DebugLog($"Enabled notifications on {issue.HtmlUrl} for {user.Name}");
                }
                catch (Exception ex)
                {
                    await Logger.DebugAsync($"Failed to enable notifications on {comment.Url} for {user.Name}: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            await Logger.DebugAsync($"Failed to enable notifications on {comment.Url}: {ex}");
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
        var users = await _users.EnterAsync();
        try
        {
            users.TryGetValue(name, out UserRecord previous);

            users[name] = new UserRecord(name, token, DateTime.UtcNow, previous?.LastSubscribedIssue ?? "N/A");
        }
        finally
        {
            _users.Exit();
        }
    }

    [GeneratedRegex(@"@[\w\/]+")]
    private static partial Regex MentionsRegex();
}
