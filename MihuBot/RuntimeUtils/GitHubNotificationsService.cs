using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using Newtonsoft.Json;
using Octokit;
using Octokit.GraphQL.Internal;
using System.Text.RegularExpressions;

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
    private readonly IConfigurationService ConfigurationService;

    public GitHubNotificationsService(Logger logger, GitHubClient github, HttpClient http, IConfigurationService configurationService)
    {
        Logger = logger;
        Github = github;
        Http = http;
        ConfigurationService = configurationService;
    }

    public async Task<bool> ProcessGitHubMentionAsync(CommentInfo comment, Issue issue = null)
    {
        bool enabledAny = false;

        try
        {
            if (ConfigurationService.GetOrDefault(null, "RuntimeUtils.NclNotifications.Disable", false))
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

                if (issue is null)
                {
                    try
                    {
                        issue = await Github.Issue.Get(comment.Issue.RepositoryId, comment.Issue.Number);
                    }
                    catch (NotFoundException)
                    {
                        _processedMentions.TryAdd(duplicationKey);
                        Logger.DebugLog($"Skipping notifications on {issue.HtmlUrl} for {user.Name} - issue not found");
                        continue;
                    }
                }

                enabledAny = true;
                bool failed = false;

                try
                {
                    var connection = new Octokit.GraphQL.Connection(
                        new Octokit.GraphQL.ProductHeaderValue("MihuBot"),
                        new InMemoryCredentialStore(user.Token),
                        Http);

                    await connection.EnableIssueNotifiactionsAsync(issue);

                    _processedMentions.TryAdd(duplicationKey);

                    Logger.DebugLog($"Enabled notifications on {issue.HtmlUrl} for {user.Name}");
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
                                LastSubscribedIssue = issue.HtmlUrl,
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

    [GeneratedRegex(@"@[\w\/]+")]
    private static partial Regex MentionsRegex();
}
