using Discord;
using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class AlcoholicsAnonymous : NonCommandHandler
    {
        private static readonly HashSet<ulong> Alcoholics = new HashSet<ulong>()
        {
            KnownUsers.Miha,
            KnownUsers.Jordan,
            KnownUsers.James,
            KnownUsers.Christian,
            KnownUsers.PaulK,
            KnownUsers.Sticky
        };

        public override Task HandleAsync(MessageContext ctx)
        {
            if ((ctx.IsFromAdmin || Alcoholics.Contains(ctx.AuthorId)) &&
                ctx.Content.StartsWith("@alcoholics", StringComparison.OrdinalIgnoreCase))
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                var alcoholics = Alcoholics.Where(a => a != ctx.AuthorId);
                await ctx.ReplyAsync(string.Join(' ', alcoholics.Select(a => MentionUtils.MentionUser(a))), suppressMentions: true);
            }
        }
    }
}
