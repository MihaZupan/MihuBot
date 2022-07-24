using Discord.Rest;

namespace MihuBot.Commands;

public sealed class SfaeLinksCommand : CommandBase
{
    private const string CommandName = "sfaelinks";
    private const string PlusOneButtonId = CommandName + "-plus-1";
    private const string MinusOneButtonId = CommandName + "-minus-1";
    private const string CloseButtonId = CommandName + "-close";

    public override string Command => CommandName;
    public override string[] Aliases => new[] { "sfaelink", "slg" };

    private readonly SynchronizedLocalJsonStore<Box<int>> _counter = new("SfaeLinks.json");
    private readonly DiscordSocketClient _discord;

    public SfaeLinksCommand(DiscordSocketClient discord)
    {
        _discord = discord;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.Guild.Id != Guilds.TheBoys)
        {
            return;
        }

        await ctx.Channel.SendMessageAsync(
            text: CurrentTally(await _counter.QueryAsync(i => i.Value)),
            components: new ComponentBuilder()
                .WithButton("+1", $"{PlusOneButtonId}-{ctx.Message.Id}")
                .WithButton("-1", $"{MinusOneButtonId}-{ctx.Message.Id}")
                .WithButton("Close", $"{CloseButtonId}-{ctx.Message.Id}", ButtonStyle.Danger)
                .Build());
    }

    public override async Task HandleMessageComponentAsync(SocketMessageComponent component)
    {
        if (component.User is null || component.User.Id == KnownUsers.Sfae)
        {
            return;
        }

        var counter = await _counter.EnterAsync();
        try
        {
            string id = component.Data.CustomId;

            if (id.StartsWith(PlusOneButtonId, StringComparison.Ordinal))
            {
                counter.Value++;
            }
            else if (id.StartsWith(MinusOneButtonId, StringComparison.Ordinal))
            {
                counter.Value--;
            }
            else if (id.StartsWith(CloseButtonId, StringComparison.Ordinal))
            {
                await component.Message.DeleteAsync();

                int prefixLength = 1 + CloseButtonId.Length;
                if (id.Length > prefixLength && ulong.TryParse(id.AsSpan(prefixLength), out ulong messageId))
                {
                    var message = await component.Channel.GetMessageAsync(messageId);
                    if (message is not null)
                    {
                        try
                        {
                            await message.DeleteAsync();
                        }
                        catch { }
                    }
                }

                return;
            }
            else
            {
                return;
            }

            await component.UpdateAsync(m => m.Content = CurrentTally(counter.Value));
        }
        finally
        {
            _counter.Exit();
        }
    }

    private static string CurrentTally(int num) => $"Current tally: {(num > 0 ? $"+{num}" : num.ToString())}";
}
