using MihuBot.Helpers;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class JoinCommand : CommandBase
    {
        public override string Command => "join";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            var vc = ctx.Guild.VoiceChannels.FirstOrDefault(vc => vc.Users.Any(u => u.Id == ctx.AuthorId));
            if (vc is null)
            {
                await ctx.ReplyAsync("Join a VC first");
                return;
            }

            await AudioClient.TryGetOrJoinAsync(ctx.Guild, vc);
        }
    }
}
