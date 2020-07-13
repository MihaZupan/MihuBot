using Discord;
using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class HusbandoAndWaifu : NonCommandHandler
    {
        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            var content = ctx.Content;

            if (content.Equals("@waifu", StringComparison.OrdinalIgnoreCase) ||
                (content.StartsWith("@husband", StringComparison.OrdinalIgnoreCase) &&
                       (content.Length == 8 || (content.Length == 9 && (content[8] | 0x20) == 'o'))))
            {
                ulong partnerId = ctx.AuthorId switch
                {
                    KnownUsers.Miha => KnownUsers.Jordan,
                    KnownUsers.Jordan => KnownUsers.Ai,
                    KnownUsers.Conor => KnownUsers.James,
                    KnownUsers.James => KnownUsers.Jordan,
                    KnownUsers.Christian => KnownUsers.James,

                    _ => 0
                };

                if (partnerId == 0)
                {
                    await ctx.ReplyAsync($"{Emotes.PepePoint}");
                }
                else
                {
                    await ctx.ReplyAsync(MentionUtils.MentionUser(partnerId));
                }
            }
        }
    }
}
