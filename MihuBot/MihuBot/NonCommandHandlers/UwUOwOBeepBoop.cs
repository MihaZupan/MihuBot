using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class UwUOwOBeepBoop : NonCommandHandler
    {
        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.Equals("uwu", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.ReplyAsync("OwO");
            }
            else if (ctx.Content.Equals("owo", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.ReplyAsync("UwU");
            }
            else if (ctx.Content.Equals("beep", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.ReplyAsync("Boop");
            }
            else if (ctx.Content.Equals("boop", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.ReplyAsync("Beep");
            }
            else if (ctx.Content.Contains("┬─┬ ノ( ゜-゜ノ)"))
            {
                await ctx.ReplyAsync("(╯°□°）╯︵ ┻━┻");
            }
            else if (ctx.Content.Contains("(╯°□°）╯︵ ┻━┻"))
            {
                await ctx.ReplyAsync("┬─┬ ノ( ゜-゜ノ)");
            }
        }
    }
}
