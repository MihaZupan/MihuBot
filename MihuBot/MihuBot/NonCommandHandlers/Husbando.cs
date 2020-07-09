using Discord;
using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class Husbando : NonCommandHandler
    {
        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            var content = ctx.Content;

            if (content.StartsWith("@husband", StringComparison.OrdinalIgnoreCase) &&
                       (content.Length == 8 || (content.Length == 9 && (content[8] | 0x20) == 'o')))
            {
                ulong husbandId = ctx.AuthorId switch
                {
                    KnownUsers.Miha => KnownUsers.Jordan,
                    KnownUsers.Jordan => KnownUsers.Miha,

                    _ => 0
                };

                if (husbandId == 0)
                {
                    await ctx.ReplyAsync($"{Emotes.DarlF}");
                }
                else
                {
                    await ctx.ReplyAsync(MentionUtils.MentionUser(husbandId));
                }
            }
        }
    }
}
