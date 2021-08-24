using MihuBot.Helpers;

namespace MihuBot.NonCommandHandlers
{
    public sealed class YesW : NonCommandHandler
    {
        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.Contains("yesw", StringComparison.OrdinalIgnoreCase))
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                await ctx.Message.AddReactionAsync(Emotes.YesW);
            }
        }
    }
}
