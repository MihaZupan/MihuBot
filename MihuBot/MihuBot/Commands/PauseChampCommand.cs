using Discord;
using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class PauseChampCommand : CommandBase
    {
        public override string Command => "pausechamp";

        protected override int CooldownToleranceCount => 0;
        protected override TimeSpan Cooldown => TimeSpan.FromDays(5);

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.ReplyAsync($"{MentionUtils.MentionUser(KnownUsers.CurtIs)} {Emotes.PauseChamp}");
        }
    }
}
