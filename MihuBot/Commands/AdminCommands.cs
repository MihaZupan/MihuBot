using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils;

namespace MihuBot.Commands;

public sealed class AdminCommands : CommandBase
{
    public override string Command => "dropingestedembeddings";
    public override string[] Aliases => ["clearingestedembeddingsupdatedat", "ingestnewrepo"];

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly GitHubDataService _gitHubDataService;

    public AdminCommands(IDbContextFactory<GitHubDbContext> db, GitHubDataService gitHubDataService)
    {
        _db = db;
        _gitHubDataService = gitHubDataService;
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

        if (ctx.Command == "clearingestedembeddingsupdatedat")
        {
            await using GitHubDbContext db = _db.CreateDbContext();
            int updates = await db.IngestedEmbeddings
                .ExecuteUpdateAsync(e => e.SetProperty(e => e.UpdatedAt, new DateTime(2010, 1, 1)));
            await ctx.ReplyAsync($"Updated {updates} ingested embeddings.");
        }

        if (ctx.Command == "ingestnewrepo")
        {
            if (ctx.Arguments.Length != 1 ||
                ctx.Arguments[0].Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is not { Length: 2 } parts)
            {
                await ctx.ReplyAsync("Usage: `ingestnewrepo owner/name`");
                return;
            }

            string repoOwner = parts[0];
            string repoName = parts[1];

            RestUserMessage message = await ctx.Channel.SendMessageAsync($"Ingesting new repository: {repoOwner}/{repoName}...");

            using var debouncer = new Debouncer<string>(TimeSpan.FromSeconds(5), async (log, ct) =>
            {
                await message.ModifyAsync(msg => msg.Content = log);
            });

            await foreach (string log in _gitHubDataService.IngestNewRepositoryAsync(repoOwner, repoName, ctx.CancellationToken))
            {
                debouncer.Update(log);
            }

            debouncer.Update($"Ingestion of {repoOwner}/{repoName} complete. Performing initial rescan ...");

            await _gitHubDataService.UpdateRepositoryDataAsync(repoOwner, repoName, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            debouncer.Update("All done");

            await Task.Delay(10_000);
        }
    }
}
