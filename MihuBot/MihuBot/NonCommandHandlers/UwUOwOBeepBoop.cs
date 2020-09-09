using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class UwUOwOBeepBoop : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(15);

        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            string response = null;

            if (ctx.Content.Equals("uwu", StringComparison.OrdinalIgnoreCase))
            {
                response = "OwO";
            }
            else if (ctx.Content.Equals("owo", StringComparison.OrdinalIgnoreCase))
            {
                response = "UwU";
            }
            else if (ctx.Content.Equals("beep", StringComparison.OrdinalIgnoreCase))
            {
                response = "Boop";
            }
            else if (ctx.Content.Equals("boop", StringComparison.OrdinalIgnoreCase))
            {
                response = "Beep";
            }
            else if (ctx.Content.Contains("┬─┬ ノ( ゜-゜ノ)"))
            {
                response = "(╯°□°）╯︵ ┻━┻";
            }
            else if (ctx.Content.Contains("(╯°□°）╯︵ ┻━┻"))
            {
                response = "┬─┬ ノ( ゜-゜ノ)";
            }

            if (response != null && TryEnter(ctx))
            {
                await ctx.ReplyAsync(response);
            }
        }
    }
}
