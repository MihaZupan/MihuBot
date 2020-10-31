using Discord;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class FakeMention : CommandBase
    {
        public override string Command => "fakemention";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync("fakemention"))
                return;

            if (ctx.Arguments.Length != 1 || !ulong.TryParse(ctx.Arguments[0], out ulong userId))
            {
                await ctx.ReplyAsync("Usage: `!fakemention UserId`");
                return;
            }

            await ctx.ReplyAsync(MentionUtils.MentionUser(userId), suppressMentions: true);
        }
    }
}
