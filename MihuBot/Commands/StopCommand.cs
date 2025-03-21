namespace MihuBot.Commands;

public sealed class StopCommand : CommandBase
{
    public override string Command => "stop";

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!await ctx.RequirePermissionAsync(ctx.Command))
            return;

        if (ctx.IsMentioned && ctx.AuthorId == KnownUsers.Miha)
        {
            await ctx.ReplyAsync("Stopping ...");
            ProgramState.BotStopTCS.TrySetResult();
        }
    }
}
