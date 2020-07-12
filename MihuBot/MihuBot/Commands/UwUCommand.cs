using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class UwUCommand : CommandBase
    {
        public override string Command => "uwu";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.ReplyAsync("Curt is a PauseChamp");
        }
    }
}
