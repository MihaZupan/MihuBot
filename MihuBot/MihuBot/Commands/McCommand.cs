namespace MihuBot.Commands;

public sealed class McCommand : CommandBase
{
    public override string Command => "mc";

    private readonly MinecraftRCON _rcon;

    public McCommand(MinecraftRCON rcon)
    {
        _rcon = rcon ?? throw new ArgumentNullException(nameof(rcon));
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!await ctx.RequirePermissionAsync("mc"))
            return;

        try
        {
            if (ctx.ArgumentString.Length > 2000 || ctx.ArgumentString.Any(c => c > 127))
            {
                await ctx.ReplyAsync("Invalid command format", mention: true);
            }
            else
            {
                string commandResponse = await _rcon.SendCommandAsync(ctx.ArgumentString);
                if (string.IsNullOrEmpty(commandResponse))
                {
                    await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                }
                else
                {
                    await ctx.ReplyAsync($"`{commandResponse}`");
                }
            }
        }
        catch (Exception ex)
        {
            await ctx.ReplyAsync("Something went wrong :/");
            await ctx.DebugAsync(ex);
        }
    }
}