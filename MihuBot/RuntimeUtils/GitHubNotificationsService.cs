using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
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
    public sealed record UserRecord(string Name, string Token, DateTime PatUpdatedAt, string LastSubscribedIssue, int ErrorCount)
    {
        [JsonIgnore]
        public bool Disabled => ErrorCount >= 5;

        public bool SubscribeToExistingIssues;
        public bool SubscribeToClosedIssues;
        public string[] Teams { get; set; }
    }

    private readonly FileBackedHashSet _processedMentions = new("ProcessedNotificationMentionIssues.txt", StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizedLocalJsonStore<Dictionary<string, UserRecord>> _users = new("GitHubNotificationUsers.json",
        init: (_, d) => new Dictionary<string, UserRecord>(d, StringComparer.OrdinalIgnoreCase));

    // "dotnet/ncl" => [area-System.Net.Http, area-System.Net.Security, ...]
    public Dictionary<string, string[]> DotnetTeamToAreaLabel = [];

    public readonly GitHubClient Github;

    private readonly Logger _logger;
    private readonly HttpClient _http;
    private readonly ServiceConfiguration _serviceConfiguration;
    private readonly IDbContextFactory<GitHubDbContext> _db;

    public GitHubNotificationsService(Logger logger, GitHubClient github, HttpClient http, IDbContextFactory<GitHubDbContext> db, ServiceConfiguration serviceConfiguration)
    {
        _logger = logger;
        Github = github;
        _http = http;
        _serviceConfiguration = serviceConfiguration;
        _db = db;

        if (!OperatingSystem.IsWindows())
        {
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(ParseDotnetRuntimeAreaOwnersTableAsync);
                _ = Task.Run(RescanOldRuntimeIssuesAsync);
                _ = Task.Run(MonitorNetworkingIssuesWithoutNclMentionAsync);
            }
        }
    }

    private async Task ParseDotnetRuntimeAreaOwnersTableAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        int consecutiveFailureCount = 0;

        do
        {
            try
            {
                string source = await _http.GetStringAsync("https://raw.githubusercontent.com/dotnet/runtime/refs/heads/main/docs/area-owners.md");
                MarkdownDocument document = MarkdownHelper.ParseAdvanced(source);
                Table table = document.Descendants<Table>().MaxBy(t => t.Span.Length);

                Dictionary<string, HashSet<string>> dotnetTeamToAreaLabels = new(StringComparer.OrdinalIgnoreCase);

                foreach (TableRow row in table.Descendants<TableRow>())
                {
                    TableCell[] columns = [.. row.Descendants<TableCell>()];

                    // | area-System.Net.Http | @karelz | @dotnet/ncl | Notes |
                    if (columns.Length < 4 || columns[0].Count != 1 || columns[2].Count != 1 ||
                        columns[0][0] is not ParagraphBlock areaPara ||
                        areaPara.Inline?.FirstChild is not LiteralInline areaLiteral ||
                        !areaLiteral.Content.AsSpan().StartsWith("area-", StringComparison.OrdinalIgnoreCase) ||
                        columns[2][0] is not ParagraphBlock teamMentionsPara ||
                        teamMentionsPara.Inline?.FirstChild is not LiteralInline teamMentionsLiteral ||
                        !teamMentionsLiteral.Content.AsSpan().Contains('@'))
                    {
                        continue;
                    }

                    string areaLabel = areaLiteral.Content.ToString().Trim();

                    foreach (string owner in teamMentionsLiteral.Content.ToString().Split(' ', StringSplitOptions.TrimEntries))
                    {
                        if (owner.StartsWith("@dotnet/", StringComparison.OrdinalIgnoreCase))
                        {
                            var areas = CollectionsMarshal.GetValueRefOrAddDefault(dotnetTeamToAreaLabels, owner.TrimStart('@'), out _) ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            areas.Add(areaLabel);
                        }
                    }
                }

                DotnetTeamToAreaLabel = dotnetTeamToAreaLabels.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

                consecutiveFailureCount = 0;
            }
            catch (Exception ex)
            {
                consecutiveFailureCount++;

                string errorMessage = $"{nameof(ParseDotnetRuntimeAreaOwnersTableAsync)}: ({consecutiveFailureCount}): {ex}";
                _logger.DebugLog(errorMessage);

                if (consecutiveFailureCount == 2)
                {
                    await _logger.DebugAsync(errorMessage);
                }
            }
        }
        while (await timer.WaitForNextTickAsync());
    }

    public async Task<bool> ProcessGitHubMentionAsync(CommentInfo comment)
    {
        bool enabledAny = false;

        try
        {
            if (_serviceConfiguration.PauseGitHubNotificationPolling)
            {
                return enabledAny;
            }

            if (comment.RepoOwner() != "dotnet")
            {
                return enabledAny;
            }

            if (!TryDetectMentions(comment.Body, out HashSet<UserRecord> users))
            {
                return enabledAny;
            }

            foreach (UserRecord user in users)
            {
                if ((DateTime.UtcNow - comment.Issue.CreatedAt > TimeSpan.FromDays(1) && !user.SubscribeToExistingIssues) ||
                    (comment.Issue.State == ItemState.Closed && !user.SubscribeToClosedIssues))
                {
                    continue;
                }

                string duplicationKey = $"{comment.Issue.HtmlUrl}/{user.Name}";

                if (_processedMentions.Contains(duplicationKey))
                {
                    continue;
                }

                if (user.Disabled)
                {
                    _logger.DebugLog($"Skipping notifications on {comment.HtmlUrl} for {user.Name} due to previous errors.");
                    continue;
                }

                enabledAny = true;
                bool failed = false;

                try
                {
                    var connection = new Octokit.GraphQL.Connection(
                        new Octokit.GraphQL.ProductHeaderValue("MihuBot"),
                        new InMemoryCredentialStore(user.Token),
                        _http);

                    await connection.EnableIssueNotifiactionsAsync(comment.Issue.Id);

                    _processedMentions.TryAdd(duplicationKey);

                    _logger.DebugLog($"Enabled notifications on {comment.Issue.HtmlUrl} for {user.Name}");
                }
                catch (Exception ex)
                {
                    failed = true;
                    await _logger.DebugAsync($"Failed to enable notifications on {comment.HtmlUrl} for {user.Name}", ex);
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
            await _logger.DebugAsync($"Failed to enable notifications on {comment.HtmlUrl}", ex);
        }

        return enabledAny;
    }

    private bool TryDetectMentions(ReadOnlySpan<char> comment, out HashSet<UserRecord> users)
    {
        users = [];

        foreach (ValueMatch match in MentionsRegex().EnumerateMatches(comment))
        {
            string name = comment.Slice(match.Index, match.Length).TrimStart('@').ToString();

            if (DotnetTeamToAreaLabel.ContainsKey(name))
            {
                GetUsersForGitHubTeam(name, users);
            }

            if (TryGetUser(name, out UserRecord user))
            {
                users.Add(user);
            }
        }

        return users.Count != 0;
    }

    private void GetUsersForGitHubTeam(string team, HashSet<UserRecord> usersToSubscribe)
    {
        _users.Query(users =>
        {
            foreach (UserRecord user in users.Values)
            {
                if (user.Teams is { } teams && teams.Contains(team, StringComparer.OrdinalIgnoreCase))
                {
                    usersToSubscribe.Add(user);
                }
            }
        });
    }

    public bool TryGetUser(string name, out UserRecord user)
    {
        user = _users.Query(users => users.TryGetValue(name, out var user) ? user : null);
        return user is not null;
    }

    public async Task UpdatePATAsync(string name, string token)
    {
        _logger.DebugLog($"Updating PAT for {name}");

        var users = await _users.EnterAsync();
        try
        {
            users.TryGetValue(name, out UserRecord previous);

            users[name] = new UserRecord(name, token, DateTime.UtcNow, previous?.LastSubscribedIssue ?? "N/A", ErrorCount: 0)
            {
                SubscribeToExistingIssues = previous?.SubscribeToExistingIssues ?? true,
                SubscribeToClosedIssues = previous?.SubscribeToClosedIssues ?? true,
                Teams = previous?.Teams ?? [],
            };
        }
        finally
        {
            _users.Exit();
        }
    }

    public async Task UpdateUserRecordAsync(UserRecord record)
    {
        _logger.DebugLog($"Updating user info for {record.Name}");

        var users = await _users.EnterAsync();
        try
        {
            users[record.Name] = record;
        }
        finally
        {
            _users.Exit();
        }
    }

    private async Task RescanOldRuntimeIssuesAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));

        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                HashSet<string> teams = await _users.QueryAsync(users =>
                {
                    var teams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (UserRecord user in users.Values)
                    {
                        if (user.SubscribeToExistingIssues)
                        {
                            teams.AddRange(user.Teams ?? []);
                        }
                    }

                    return teams;
                });

                string[] areas = teams
                    .SelectMany(t => DotnetTeamToAreaLabel.TryGetValue(t, out string[] areas) ? areas : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                await RescanOldRuntimeIssuesAsync(areas, new DateTime(2000, 1, 1, 1, 1, 1, DateTimeKind.Utc));
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync(nameof(RescanOldRuntimeIssuesAsync), ex);
            }
        }
    }

    public async Task RescanOldRuntimeIssuesAsync(string[] areaLabels, DateTime since, CancellationToken cancellationToken = default)
    {
        await using GitHubDbContext db = _db.CreateDbContext();

        string[] labelIds = (await db.Labels
            .AsNoTracking()
            .Select(l => new { l.Id, l.Name })
            .ToArrayAsync(cancellationToken))
            .Where(label => areaLabels.Contains(label.Name, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Id)
            .ToArray();

        string[] issueIds = await db.Issues
            .AsNoTracking()
            .Where(i => i.CreatedAt >= since)
            .FromDotnetRuntime()
            .Where(i => i.Labels.Any(l => labelIds.Contains(l.Id)))
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => i.Id)
            .ToArrayAsync(cancellationToken);

        User currentUser = await Github.User.Current();

        int counter = 0;

        foreach (string[] chunk in issueIds.Chunk(1_000))
        {
            IssueInfo[] issues = await db.Issues
                .AsNoTracking()
                .Where(i => chunk.Contains(i.Id))
                .Include(i => i.Labels)
                .Include(i => i.Repository)
                    .ThenInclude(r => r.Owner)
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken);

            foreach (IssueInfo issue in issues)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string[] groups = issue.Labels
                    .Where(l => areaLabels.Contains(l.Name, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(l => DotnetTeamToAreaLabel.Where(p => p.Value.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).Select(p => p.Key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                bool subscribedAny = await ProcessGitHubMentionAsync(new CommentInfo
                {
                    Id = "210716005/1",
                    HtmlUrl = issue.HtmlUrl,
                    Body = $"{string.Join(", ", groups.Select(g => $"@{g}"))}  --  {nameof(RescanOldRuntimeIssuesAsync)}",
                    Issue = issue,
                    UserId = currentUser.Id,
                    User = new UserInfo
                    {
                        Id = currentUser.Id,
                        Login = currentUser.Login,
                        Name = currentUser.Name,
                    },
                    IsPrReviewComment = false,
                });

                if (subscribedAny)
                {
                    counter++;
                    await Task.Delay(2_000, cancellationToken);
                }

                await Task.Delay(10, cancellationToken);
            }
        }

        if (counter > 0)
        {
            await _logger.DebugAsync($"[{nameof(RescanOldRuntimeIssuesAsync)}]: Subscribed to notifications on {counter} out of {issueIds.Length} issues.");
        }
    }

    private async Task MonitorNetworkingIssuesWithoutNclMentionAsync()
    {
        try
        {
            TimeSpan maxTimeFromLabelToMention = TimeSpan.FromMinutes(10);

            using var timer = new PeriodicTimer(maxTimeFromLabelToMention / 4);

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    if (_serviceConfiguration.PauseGitHubNCLMentionPolling || _serviceConfiguration.PauseGitHubPolling)
                    {
                        continue;
                    }

                    await using GitHubDbContext db = _db.CreateDbContext();

                    DateTime onlyCreatedInLastMonth = DateTime.UtcNow - TimeSpan.FromDays(30);
                    DateTime skipRecentlyCreated = DateTime.UtcNow - maxTimeFromLabelToMention;

                    Stopwatch queryStopwatch = Stopwatch.StartNew();

                    IssueInfo[] networkingIssues = await db.Issues
                        .AsNoTracking()
                        .Where(i => i.CreatedAt >= onlyCreatedInLastMonth && i.CreatedAt <= skipRecentlyCreated)
                        .Where(i => i.Labels.Any(l => Constants.NetworkingLabels.Any(nl => nl == l.Name)))
                        .Where(i => i.IssueType == IssueType.Issue)
                        .Where(i => i.State == ItemState.Open)
                        .FromDotnetRuntime()
                        .Include(i => i.Comments)
                        .OrderByDescending(i => i.CreatedAt)
                        .Take(1000)
                        .AsSplitQuery()
                        .ToArrayAsync();

                    ServiceInfo.LastGitHubNCLMentionsQueryTime = queryStopwatch.Elapsed;

                    foreach (IssueInfo issue in networkingIssues)
                    {
                        string dupeKey = $"NoNCLMention/{issue.HtmlUrl}";

                        if (_processedMentions.Contains(dupeKey))
                        {
                            continue;
                        }

                        if (issue.Comments.Count >= 10 ||
                            issue.Comments.Any(c => c.Body.Contains("@dotnet/ncl", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        try
                        {
                            IReadOnlyList<IssueEvent> events = await Github.Issue.Events.GetAllForIssue(issue.RepositoryId, issue.Number);

                            bool areaLabelIsRecent = false;

                            foreach (IssueEvent e in events)
                            {
                                if (e.Event.Value == EventInfoState.Labeled &&
                                    e.Label?.Name is { } labelName &&
                                    Constants.NetworkingLabels.Contains(labelName) &&
                                    (DateTime.UtcNow - e.CreatedAt) < maxTimeFromLabelToMention)
                                {
                                    areaLabelIsRecent = true;
                                    break;
                                }
                            }

                            if (areaLabelIsRecent)
                            {
                                // Give the default policy bot some time to comment.
                                // We'll check later in case it hasn't.
                                continue;
                            }

                            IReadOnlyList<IssueComment> updatedComments = await Github.Issue.Comment.GetAllForIssue(issue.RepositoryId, issue.Number);

                            if (updatedComments.Any(c => c.Body.Contains("@dotnet/ncl", StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.DebugLog($"Failed to get updated issue info for <{issue.HtmlUrl}>: {ex}");
                            continue;
                        }

                        if (!_processedMentions.TryAdd(dupeKey))
                        {
                            continue;
                        }

                        _logger.DebugLog($"[{nameof(MonitorNetworkingIssuesWithoutNclMentionAsync)}]: No NCL mention in {issue.HtmlUrl}");

                        await Github.Issue.Comment.Create(issue.RepositoryId, issue.Number, "cc: @dotnet/ncl");
                    }

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    string errorMessage = $"{nameof(MonitorNetworkingIssuesWithoutNclMentionAsync)}: ({consecutiveFailureCount}): {ex}";
                    _logger.DebugLog(errorMessage);

                    await Task.Delay(TimeSpan.FromMinutes(5) * consecutiveFailureCount);

                    if (consecutiveFailureCount == 2)
                    {
                        await _logger.DebugAsync(errorMessage);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected exception: {ex}");
        }
    }

    [GeneratedRegex(@"@[\w\/\-]+")]
    private static partial Regex MentionsRegex();
}
