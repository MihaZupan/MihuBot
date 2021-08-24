using System.Runtime.InteropServices;

namespace MihuBot.Commands
{
    public sealed class VersionCommand : CommandBase
    {
        public override string Command => "version";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.ReplyAsync($"`{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.RuntimeIdentifier}`");
        }
    }
}
