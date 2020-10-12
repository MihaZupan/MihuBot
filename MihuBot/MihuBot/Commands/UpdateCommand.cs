using MihuBot.Helpers;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class UpdateCommand : CommandBase
    {
        public override string Command => "update";
        public override string[] Aliases => new[] { "stop" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (ctx.IsFromAdmin && ctx.IsMentioned)
            {
                if (ctx.Command == "update")
                {
                    _ = Task.Run(Program.StartUpdate);
                }
                else if (ctx.AuthorId == KnownUsers.Miha)
                {
                    await ctx.ReplyAsync("Stopping ...");
                    Program.BotStopTCS.TrySetResult(null);
                }
            }
        }
    }
}
