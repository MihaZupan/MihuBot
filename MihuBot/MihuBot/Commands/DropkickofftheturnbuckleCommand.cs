using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class DropkickofftheturnbuckleCommand : CommandBase
    {
        public override string Command => "dropkickofftheturnbuckle";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.ReplyAsync("Hello Curt");
        }
    }
}
