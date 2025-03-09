namespace MihuBot.NonCommandHandlers;

public class Civ : NonCommandHandler
{
    public override Task HandleAsync(MessageContext ctx)
    {
        if (ctx.Guild.Id == Guilds.TheBoys &&
            ctx.Content.Contains("civ", StringComparison.OrdinalIgnoreCase) &&
            !ctx.Content.Contains("://", StringComparison.Ordinal) &&
            Rng.Chance(5))
        {
            return HandleAsyncCore();
        }

        return Task.CompletedTask;

        async Task HandleAsyncCore()
        {
            await ctx.Message.AddReactionAsync(Emotes.SomeoneSayCiv);
        }
    }
}
