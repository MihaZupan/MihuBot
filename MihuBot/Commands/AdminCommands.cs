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
            await db.IngestedEmbeddings.ExecuteDeleteAsync();
        }
    }
}
