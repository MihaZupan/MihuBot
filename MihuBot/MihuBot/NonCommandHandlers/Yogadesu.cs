using System;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class Yogadesu : NonCommandHandler
    {
        private int _yogadesuCounter = 0;

        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.StartsWith("@yogadesu", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.ReplyAsync($"Y{new string('o', Interlocked.Increment(ref _yogadesuCounter))}gades");
            }
        }
    }
}
