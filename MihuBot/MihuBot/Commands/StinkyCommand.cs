using Discord;
using MihuBot.Helpers;

namespace MihuBot.Commands
{
    public sealed class StinkyCommand : CommandBase
    {
        public override string Command => "stinky";

        protected override TimeSpan Cooldown => TimeSpan.FromDays(1);
        protected override int CooldownToleranceCount => 0;

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            ulong user = new[] { KnownUsers.Adi, KnownUsers.Joster, KnownUsers.Jordan }.Random();
            await ctx.ReplyAsync(MentionUtils.MentionUser(user), suppressMentions: user != KnownUsers.Adi);
        }
    }
}
