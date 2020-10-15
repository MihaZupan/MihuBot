using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class GoodBot : NonCommandHandler
    {
        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.Equals("good bot", StringComparison.OrdinalIgnoreCase))
                return HandleAsyncCore();

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                await ctx.Message.AddReactionAsync(Emotes.DarlPatPat);
            }
        }
    }
}
