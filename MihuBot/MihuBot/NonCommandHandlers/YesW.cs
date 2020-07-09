using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class YesW : NonCommandHandler
    {
        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.Contains("yesw", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.Message.AddReactionAsync(Emotes.YesW);
            }
        }
    }
}
