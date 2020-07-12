using MihuBot.Helpers;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class PineappleCommand : CommandBase
    {
        public override string Command => "pineapple";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (ctx.Guild.Id == Guilds.LiverGang)
            {
                await ctx.ReplyAsync("https://clips.twitch.tv/CredulousSteamyDolphinKAPOW");
            }
        }
    }
}
