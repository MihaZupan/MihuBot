using Discord;
using MihuBot.Helpers;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class PauseChampCommand : CommandBase
    {
        public override string Command => "pausechamp";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.ReplyAsync($"{MentionUtils.MentionUser(KnownUsers.CurtIs)} {Emotes.PauseChamp}");
        }
    }
}
