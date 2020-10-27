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

            await ctx.ReplyAsync("I listen to:\n" +
                string.Join(", ", ctx.Guild.Users.Where(u => u.IsAdmin()).Select(a => a.GetName())));
        }
    }
}
