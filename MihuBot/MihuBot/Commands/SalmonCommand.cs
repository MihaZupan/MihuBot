using Discord;
using MihuBot.Helpers;

namespace MihuBot.Commands
{
    public sealed class SalmonCommand : CommandBase
    {
        public override string Command => "salmon";

        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(5);
        protected override int CooldownToleranceCount => 5;

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.ReplyAsync(MentionUtils.MentionUser(KnownUsers.Joster), suppressMentions: true);
        }
    }
}
