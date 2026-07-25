namespace MihuBot.Commands;

public sealed class SfaeCommand : CommandBase
{
    public override string Command => "sfae";

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        string query = ctx.ArgumentString.Replace(' ', '+');

        await Task.WhenAll(
            ctx.ReplyAsync($"https://letmegooglethat.com/?q={Uri.EscapeDataString(query)}"),
            ctx.Channel.DeleteMessageAsync(ctx.Message));
    }
}
