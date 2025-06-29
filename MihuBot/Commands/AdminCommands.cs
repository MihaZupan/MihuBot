using Discord.Rest;
using Microsoft.EntityFrameworkCore;
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

    public AdminCommands(IDbContextFactory<GitHubDbContext> db, GitHubDataService gitHubDataService, GitHubSearchService gitHubSearchService, IssueTriageService triageService, IssueTriageHelper triageHelper, IDbContextFactory<GitHubFtsDbContext> dbFts, IDbContextFactory<MihuBotDbContext> dbMihuBot, IDbContextFactory<LogsDbContext> dbLogs, HybridCache cache)
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

        if (ctx.Command == "forcetriage")
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

            Uri issueUrl = await _triageService.ManualTriageAsync(issue, ctx.CancellationToken);
            await ctx.ReplyAsync($"Triage completed. See the issue at <{issueUrl.AbsoluteUri}>.");
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
}
