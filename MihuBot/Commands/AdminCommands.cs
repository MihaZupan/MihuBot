using System.Collections.Concurrent;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.DB.GitHubFts;
using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class AdminCommands : CommandBase
{
    public override string Command => "dropingestedembeddings";
    public override string[] Aliases =>
    [
        "dropingestedftsrecords",
        "clearingestedembeddingsupdatedat",
        "clearbodyedithistorytable",
        "deleteissueandembeddings",
        "ingestnewrepo",
        "ingestnewreposcan",
        "forcetriage",
        "dumpdbcounts",
        "clearhybridcache-search",
        "duplicates",
    ];

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly IDbContextFactory<GitHubFtsDbContext> _dbFts;
    private readonly IDbContextFactory<MihuBotDbContext> _dbMihuBot;
    private readonly IDbContextFactory<LogsDbContext> _dbLogs;
    private readonly GitHubDataService _gitHubDataService;
    private readonly GitHubSearchService _gitHubSearchService;
    private readonly IssueTriageService _triageService;
    private readonly IssueTriageHelper _triageHelper;
    private readonly HybridCache _cache;
    private readonly ServiceConfiguration _serviceConfiguration;
    private readonly Logger _logger;

    private readonly FileBackedHashSet _processedIssuesForDuplicateDetection = new("ProcessedIssuessForDuplicateDetection.txt");

    public AdminCommands(IDbContextFactory<GitHubDbContext> db, GitHubDataService gitHubDataService, GitHubSearchService gitHubSearchService, IssueTriageService triageService, IssueTriageHelper triageHelper, IDbContextFactory<GitHubFtsDbContext> dbFts, IDbContextFactory<MihuBotDbContext> dbMihuBot, IDbContextFactory<LogsDbContext> dbLogs, HybridCache cache, ServiceConfiguration serviceConfiguration, Logger logger)
    {
        _db = db;
        _gitHubDataService = gitHubDataService;
        _gitHubSearchService = gitHubSearchService;
        _triageService = triageService;
        _triageHelper = triageHelper;
        _dbFts = dbFts;
        _dbMihuBot = dbMihuBot;
        _dbLogs = dbLogs;
        _cache = cache;
        _serviceConfiguration = serviceConfiguration;
        _logger = logger;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ctx.IsFromAdmin)
        {
            return;
        }

        if (ctx.Command == "dropingestedembeddings")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
            int updates = await db.IngestedEmbeddings.ExecuteDeleteAsync();
            await ctx.ReplyAsync($"Deleted {updates} ingested embeddings.");
        }

        if (ctx.Command == "dropingestedftsrecords")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
            int updates = await db.IngestedFullTextSearchRecords.ExecuteDeleteAsync();
            await ctx.ReplyAsync($"Deleted {updates} ingested FTS records.");
        }

        if (ctx.Command == "clearingestedembeddingsupdatedat")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
            int updates = await db.IngestedEmbeddings
                .ExecuteUpdateAsync(e => e.SetProperty(e => e.UpdatedAt, new DateTime(2010, 1, 1)));
            await ctx.ReplyAsync($"Updated {updates} ingested embeddings.");
        }

        if (ctx.Command == "clearbodyedithistorytable")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
            int updates = await db.BodyEditHistory.ExecuteDeleteAsync();
            await ctx.ReplyAsync($"Deleted {updates} body edit history entries.");
        }

        if (ctx.Command == "deleteissueandembeddings")
        {
            if (ctx.Arguments.Length != 1)
            {
                await ctx.ReplyAsync("Usage: `deleteissueandembeddings <issueId>`");
                return;
            }

            string message = await _gitHubSearchService.DeleteIssueAndEmbeddingsAsync(ctx.Arguments[0]);
            await ctx.ReplyAsync(message);
        }

        if (ctx.Command is "ingestnewrepo" or "ingestnewreposcan")
        {
            bool scanOnly = ctx.Command == "ingestnewreposcan";

            if (ctx.Arguments.Length is 0 or > 4 ||
                ctx.Arguments[0].Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is not { Length: 2 } parts)
            {
                await ctx.ReplyAsync("Usage: `ingestnewrepo owner/name`");
                return;
            }

            string repoOwner = parts[0];
            string repoName = parts[1];

            int initialIssueNumber = ctx.Arguments.Length >= 2
                ? int.Parse(ctx.Arguments[1])
                : -1;

            int targetApiRate = ctx.Arguments.Length >= 3
                ? int.Parse(ctx.Arguments[2])
                : 4000;

            string alternativeClientKey = ctx.Arguments.Length >= 4
                ? ctx.Arguments[3]
                : null;

            GitHubClient alternativeClient = alternativeClientKey is null ? null : new GitHubClient(new ProductHeaderValue("MihuBot"))
            {
                Credentials = new Credentials(alternativeClientKey)
            };

            RestUserMessage message = await ctx.Channel.SendMessageAsync($"Ingesting new repository: {repoOwner}/{repoName}...");

            using var debouncer = new Debouncer<string>(TimeSpan.FromSeconds(30), async (log, ct) =>
            {
                await message.ModifyAsync(msg => msg.Content = log);
            });

            Stopwatch stopwatch = Stopwatch.StartNew();

            int previousDbUpdates = 0;
            int newDbUpdates = 0;
            int rescans = 0;

            do
            {
                rescans++;
                stopwatch.Restart();
                previousDbUpdates = newDbUpdates;

                await foreach ((string log, _, int dbUpdates) in _gitHubDataService.IngestNewRepositoryAsync(repoOwner, repoName, initialIssueNumber, targetApiRate, alternativeClient, ctx.CancellationToken))
                {
                    newDbUpdates = dbUpdates;
                    debouncer.Update(log);
                }
            }
            while (!scanOnly && stopwatch.Elapsed.TotalHours > 3 && (newDbUpdates - previousDbUpdates) > 5_000 && rescans <= 2);

            if (!scanOnly)
            {
                debouncer.Update($"Ingestion of {repoOwner}/{repoName} complete. Performing initial rescan after delay...");

                await Task.Delay(5_000, ctx.CancellationToken);

                await _gitHubDataService.UpdateRepositoryDataAsync(repoOwner, repoName, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), targetApiRate, alternativeClient);
            }

            debouncer.Update("All done");

            await Task.Delay(20_000, ctx.CancellationToken);
        }

        if (ctx.Command is "forcetriage" or "duplicates")
        {
            if (ctx.Arguments.Length != 1 || !GitHubHelper.TryParseIssueOrPRNumber(ctx.Arguments[0], out string repoName, out int issueNumber))
            {
                await ctx.ReplyAsync("Invalid issue/PR URL. Use the number or the full link.");
                return;
            }

            IssueInfo issue = await _triageHelper.GetIssueAsync(repoName ?? "dotnet/runtime", issueNumber, ctx.CancellationToken);

            if (issue is null)
            {
                await ctx.ReplyAsync("Issue not found in database.");
                return;
            }

            if (ctx.Command == "duplicates")
            {
                (IssueInfo Issue, double Certainty, string Summary)[] duplicates = await DetectIssueDuplicatesAsync(issue, ctx.CancellationToken);

                string reply = duplicates.Length == 0
                    ? "No duplicates found."
                    : FormatDuplicatesSummary(issue, duplicates);

                if (reply.Length <= 1800)
                {
                    await ctx.ReplyAsync(reply);
                }
                else
                {
                    await ctx.Channel.SendTextFileAsync($"Duplicates-{issue.Number}.txt", reply);
                }
            }
            else
            {
                Uri issueUrl = await _triageService.ManualTriageAsync(issue, ctx.CancellationToken);
                await ctx.ReplyAsync($"Triage completed. See the issue at <{issueUrl.AbsoluteUri}>.");
            }
        }

        if (ctx.Command == "dumpdbcounts")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
            await using GitHubFtsDbContext dbFts = _dbFts.CreateDbContext();
            await using MihuBotDbContext dbMihuBot = _dbMihuBot.CreateDbContext();
            await using LogsDbContext dbLogs = _dbLogs.CreateDbContext();

            (string Name, Func<Task<int>> CountCallback)[] tables =
            [
                ("Issues", () => db.Issues.CountAsync()),
                ("Repositories", () => db.Repositories.CountAsync()),
                ("PullRequests", () => db.PullRequests.CountAsync()),
                ("Comments", () => db.Comments.CountAsync()),
                ("Users", () => db.Users.CountAsync()),
                ("Labels", () => db.Labels.CountAsync()),
                ("BodyEditHistory", () => db.BodyEditHistory.CountAsync()),
                ("IngestedEmbeddings", () => db.IngestedEmbeddings.CountAsync()),
                ("IngestedFullTextSearchRecords", () => db.IngestedFullTextSearchRecords.CountAsync()),
                ("TriagedIssues", () => db.TriagedIssues.CountAsync()),
                ("TextEntries", () => dbFts.TextEntries.CountAsync()),
                ("Logs", () => dbLogs.Logs.CountAsync()),
                ("Reminders", () => dbMihuBot.Reminders.CountAsync()),
                ("CompletedJobs", () => dbMihuBot.CompletedJobs.CountAsync()),
                ("UrlShortenerEntries", () => dbMihuBot.UrlShortenerEntries.CountAsync()),
                ("UserLocations", () => dbMihuBot.UserLocation.CountAsync()),
                ("CoreRootEntries", () => dbMihuBot.CoreRoot.CountAsync())
            ];

            if (ctx.Arguments.Length > 0)
            {
                tables = [.. tables.Where(t => ctx.Arguments.Any(a => t.Name.Contains(a, StringComparison.OrdinalIgnoreCase)))];

                if (tables.Length == 0)
                {
                    await ctx.ReplyAsync("No tables matched the provided arguments.");
                    return;
                }
            }

            List<(string Name, int Count)> counts = [];

            foreach ((string name, Func<Task<int>> countCallback) in tables)
            {
                counts.Add((name, await countCallback()));
            }

            await ctx.ReplyAsync($"**Database counts:**\n{string.Join('\n', counts.OrderBy(c => c.Name).Select(c => $"{c.Name}: {c.Count}"))}");
        }

        if (ctx.Command == "clearhybridcache-search")
        {
            await _cache.RemoveByTagAsync(nameof(GitHubSearchService));
            await ctx.ReplyAsync("Hybrid cache cleared.");
        }
    }

    public override Task InitAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                var sempahore = new SemaphoreSlim(2, 2);

                while (await timer.WaitForNextTickAsync())
                {
                    try
                    {
                        if (_serviceConfiguration.PauseAutoDuplicateDetection)
                        {
                            continue;
                        }

                        await using GitHubDbContext db = _db.CreateDbContext();

                        DateTime startDate = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(5));

                        IQueryable<IssueInfo> query = db.Issues
                            .AsNoTracking()
                            .Where(i => i.CreatedAt >= startDate)
                            .OrderByDescending(i => i.CreatedAt);

                        query = IssueTriageHelper.AddIssueInfoIncludes(query);

                        IssueInfo[] issues = await query
                            .Take(100)
                            .AsSplitQuery()
                            .ToArrayAsync();

                        foreach (IssueInfo issue in issues)
                        {
                            if (issue.PullRequest is not null)
                            {
                                continue;
                            }

                            if (!_processedIssuesForDuplicateDetection.TryAdd(issue.Id))
                            {
                                continue;
                            }

                            _ = Task.Run(async () =>
                            {
                                await sempahore.WaitAsync();
                                try
                                {
                                    (IssueInfo Issue, double Certainty, string Summary)[] duplicates = await DetectIssueDuplicatesAsync(issue, CancellationToken.None);

                                    if (duplicates.Length > 0)
                                    {
                                        SocketTextChannel channel = _logger.Options.Discord.GetTextChannel(1396832159888703498UL);

                                        string reply = FormatDuplicatesSummary(issue, duplicates);

                                        string mention = duplicates.Any(d => d.Certainty >= 0.95 && !issue.Body.Contains(d.Issue.Number.ToString()))
                                            ? MentionUtils.MentionUser(KnownUsers.Miha)
                                            : null;

                                        if (reply.Length <= 1800)
                                        {
                                            await channel.SendMessageAsync($"{mention} {reply}".Trim());
                                        }
                                        else
                                        {
                                            await channel.SendTextFileAsync($"Duplicates-{issue.Number}.txt", reply, mention);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    await _logger.DebugAsync($"{nameof(AdminCommands)}: Error during duplicate detection for issue <{issue.HtmlUrl}>", ex);
                                }
                                finally
                                {
                                    sempahore.Release();
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync($"{nameof(AdminCommands)}: Error during periodic duplicate detection", ex);
                    }
                }
            }
            catch { }
        });

        return Task.CompletedTask;
    }

    private async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectIssueDuplicatesAsync(IssueInfo issue, CancellationToken cancellationToken)
    {
        var options = new IssueTriageHelper.TriageOptions(_triageHelper.DefaultModel, "MihaZupan", issue, OnToolLog: i => { }, SkipCommentsOnCurrentIssue: true);

        return await _triageHelper.DetectDuplicateIssuesAsync(options, cancellationToken);
    }

    private static string FormatDuplicatesSummary(IssueInfo issue, (IssueInfo Issue, double Certainty, string Summary)[] duplicates)
    {
        return $"Duplicate issues for {issue.Repository.FullName}#{issue.Number} - {issue.Title}:\n" +
            string.Join('\n', duplicates.Select(r => $"- ({r.Certainty:F2}) [#{r.Issue.Number} - {r.Issue.Title}](<{r.Issue.HtmlUrl}>)\n  - {r.Summary}"));
    }
}
