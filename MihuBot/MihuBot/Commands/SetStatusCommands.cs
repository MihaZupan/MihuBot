using Discord;
using System;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class SetStatusCommands : CommandBase
    {
        public override string Command => "setstatus";
        public override string[] Aliases => new[] { "setplaying", "setlistening", "setwatching", "setstreaming" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            string name = ctx.ArgumentString, streamUrl = null;
            ActivityType type;

            if (ctx.Command == "setstatus")
            {
                type = ActivityType.CustomStatus;
            }
            else if (ctx.Command == "setplaying")
            {
                type = ActivityType.Playing;
            }
            else if (ctx.Command == "setlistening")
            {
                type = ActivityType.Listening;
            }
            else if (ctx.Command == "setwatching")
            {
                type = ActivityType.Watching;
            }
            else if (ctx.Command == "setstreaming")
            {
                var split = ctx.ArgumentString.Split(';', StringSplitOptions.RemoveEmptyEntries);
                name = split[0];
                streamUrl = split.Length > 1 ? split[1] : null;
                type = ActivityType.Streaming;
            }
            else throw new InvalidOperationException(ctx.Command);

            await ctx.Discord.SetGameAsync(name, streamUrl, type);
        }
    }
}
