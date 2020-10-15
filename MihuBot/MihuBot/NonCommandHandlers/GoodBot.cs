using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class GoodBot : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);
        protected override int CooldownToleranceCount => 30;

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.Equals("good bot", StringComparison.OrdinalIgnoreCase) && TryEnter(ctx))
                return HandleAsyncCore();

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                await ctx.Message.AddReactionAsync(Emotes.DarlPatPat);
            }
        }
    }
}
