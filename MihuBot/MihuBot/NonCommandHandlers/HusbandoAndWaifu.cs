using Discord;
using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class HusbandoAndWaifu : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            var content = ctx.Content;

            bool waifu = content.Equals("@waifu", StringComparison.OrdinalIgnoreCase);

            bool husbando = !waifu &&
                (content.StartsWith("@husband", StringComparison.OrdinalIgnoreCase) &&
                (content.Length == 8 || (content.Length == 9 && (content[8] | 0x20) == 'o')));

            if (waifu || husbando)
            {
                if (!await TryEnterOrWarnAsync(ctx))
                    return;

                ulong partnerId = ctx.AuthorId switch
                {
                    KnownUsers.Miha => new[] { KnownUsers.Jordan, KnownUsers.James, KnownUsers.Sticky }.Random(),
                    KnownUsers.Jordan => waifu ? KnownUsers.Ai : KnownUsers.James,
                    KnownUsers.James => waifu ? KnownUsers.Maric : KnownUsers.Jordan,
                    KnownUsers.Maric => KnownUsers.James,
                    KnownUsers.Christian => KnownUsers.James,
                    KnownUsers.Conor => KnownUsers.James,

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
