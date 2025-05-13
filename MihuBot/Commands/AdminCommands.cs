using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;

namespace MihuBot.Commands;

public sealed class AdminCommands : CommandBase
{
    public override string Command => "dropingestedembeddings";

    private readonly IDbContextFactory<GitHubDbContext> _db;

    public AdminCommands(IDbContextFactory<GitHubDbContext> db) => _db = db;

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
                .ExecuteUpdateAsync(e => e.SetProperty(e => e.UpdatedAt, new DateTime(2020, 1, 1)));
            await ctx.ReplyAsync($"Updated {updates} ingested embeddings.");
        }
    }
}
