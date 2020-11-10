using Discord;
using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class Boner : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.StartsWith("@boner", StringComparison.OrdinalIgnoreCase))
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                if (!await TryEnterOrWarnAsync(ctx))
                    return;

                await ctx.ReplyAsync(MentionUtils.MentionUser(KnownUsers.Joster), suppressMentions: true);
            }
        }
    }
}
