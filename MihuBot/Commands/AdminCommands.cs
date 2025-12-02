using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.Search;

namespace MihuBot.Commands;

public sealed class AdminCommands : CommandBase
{
    public override string Command => "dumpdbcounts";
    public override string[] Aliases =>
    [
        "clearbodyedithistorytable",
        "clearhybridcache-search",
        "deleteolddebuglogs",
    ];

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly IDbContextFactory<MihuBotDbContext> _dbMihuBot;
    private readonly IDbContextFactory<LogsDbContext> _dbLogs;
    private readonly HybridCache _cache;
    private readonly Logger _logger;

    public AdminCommands(IDbContextFactory<GitHubDbContext> db, IDbContextFactory<MihuBotDbContext> dbMihuBot, IDbContextFactory<LogsDbContext> dbLogs, HybridCache cache, Logger logger)
    {
        _db = db;
        _dbMihuBot = dbMihuBot;
        _dbLogs = dbLogs;
        _cache = cache;
        _logger = logger;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ctx.IsFromAdmin)
        {
            return;
        }

        if (ctx.Command == "clearbodyedithistorytable")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
            int updates = await db.BodyEditHistory.ExecuteDeleteAsync();
            await ctx.ReplyAsync($"Deleted {updates} body edit history entries.");
        }

        if (ctx.Command == "dumpdbcounts")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
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
                ("Milestones", () => db.Milestones.CountAsync()),
                ("BodyEditHistory", () => db.BodyEditHistory.CountAsync()),
                ("TriagedIssues", () => db.TriagedIssues.CountAsync()),
                ("SemanticIngestionBacklog", () => db.SemanticIngestionBacklog.CountAsync()),
                ("IngestedEmbeddings", () => db.IngestedEmbeddings.CountAsync()),
                ("TextEntries", () => db.TextEntries.CountAsync()),
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

        if (ctx.Command == "deleteolddebuglogs")
        {
            int deleted = await _logger.DeleteDebugLogsAsync(int.Parse(ctx.Arguments[0]), int.Parse(ctx.Arguments[1]));
            await ctx.ReplyAsync($"Deleted {deleted} old debug log entries.");
        }
    }
}
