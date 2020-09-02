using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class LogsCommand : CommandBase
    {
        public override string Command => "logs";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            bool reset = ctx.Arguments.Any(arg => arg.Equals("reset", StringComparison.OrdinalIgnoreCase));

            Logger logger = ctx.Services.Logger;
            await logger.SendLogFilesAsync(logger.LogsReportsTextChannel, resetLogFiles: reset);
        }
    }
}
