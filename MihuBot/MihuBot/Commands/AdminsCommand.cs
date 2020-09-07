using MihuBot.Helpers;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class AdminsCommand : CommandBase
    {
        public override string Command => "admins";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            var guild = ctx.Guild;
            await ctx.ReplyAsync("I listen to:\n" + string.Join(", ", guild.Users.Where(u => u.IsAdminFor(guild.Id)).Select(a => a.Username)));
        }
    }
}
