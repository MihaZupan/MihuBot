using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class VersionCommand : CommandBase
    {
        public override string Command => "version";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            string os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
            string rid = RuntimeInformation.RuntimeIdentifier;
            await ctx.ReplyAsync($"{RuntimeInformation.FrameworkDescription} on {os} - `{rid}`");
        }
    }
}
