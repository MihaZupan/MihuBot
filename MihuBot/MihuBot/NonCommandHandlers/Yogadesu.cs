using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class Yogadesu : NonCommandHandler
    {
        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.StartsWith("@yogadesu", StringComparison.OrdinalIgnoreCase))
            {
                int count = (int)await ctx.Redis.StringIncrementAsync("counters-yogadesu");
                count = Math.Max(count, 1024);

                await ctx.ReplyAsync($"Y{new string('o', count)}gades");
            }
        }
    }
}
