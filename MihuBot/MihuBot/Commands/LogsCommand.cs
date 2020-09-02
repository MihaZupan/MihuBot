using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

            bool reset = ctx.Arguments.Length == 1 && ctx.Arguments[0].Equals("reset", StringComparison.OrdinalIgnoreCase);

            Logger logger = ctx.Services.Logger;

            if (ctx.Arguments.Length == 0 || reset)
            {
                await logger.SendLogFilesAsync(logger.LogsReportsTextChannel, resetLogFiles: reset);
            }
            else
            {
                DateTime after = new DateTime(2000, 1, 1);
                DateTime before = new DateTime(3000, 1, 1);

                var afterDateMatch = Regex.Match(ctx.ArgumentStringTrimmed, @"^after:? (\d\d\d\d)-(\d\d)-(\d\d)(?: (\d\d)-(\d\d)-(\d\d))?$", RegexOptions.IgnoreCase);
                if (afterDateMatch.Success)
                {
                    var groups = afterDateMatch.Groups;
                    after = new DateTime(int.Parse(groups[1].Value), int.Parse(groups[2].Value), int.Parse(groups[3].Value));
                    if (groups[4].Success)
                    {
                        after = after.AddHours(int.Parse(groups[4].Value)).AddMinutes(int.Parse(groups[5].Value)).AddSeconds(int.Parse(groups[6].Value));
                    }
                }

                var beforeDateMatch = Regex.Match(ctx.ArgumentStringTrimmed, @"^before:? (\d\d\d\d)-(\d\d)-(\d\d)(?: (\d\d)-(\d\d)-(\d\d))?$", RegexOptions.IgnoreCase);
                if (beforeDateMatch.Success)
                {
                    var groups = beforeDateMatch.Groups;
                    before = new DateTime(int.Parse(groups[1].Value), int.Parse(groups[2].Value), int.Parse(groups[3].Value));
                    if (groups[4].Success)
                    {
                        before = before.AddHours(int.Parse(groups[4].Value)).AddMinutes(int.Parse(groups[5].Value)).AddSeconds(int.Parse(groups[6].Value));
                    }
                }

                if (after >= before)
                {
                    await ctx.ReplyAsync("'After' must be earlier in time than 'Before'", mention: true);
                    return;
                }

                Predicate<Logger.LogEvent> predicate = el => el.Type == Logger.EventType.MessageReceived || el.Type == Logger.EventType.MessageUpdated;

                var inMatch = Regex.Match(ctx.ArgumentStringTrimmed, @"^in:? (\d+?) (\d+?)$", RegexOptions.IgnoreCase);
                if (inMatch.Success &&
                    ulong.TryParse(inMatch.Groups[1].Value, out ulong guildId) &&
                    ulong.TryParse(inMatch.Groups[2].Value, out ulong channelId))
                {
                    var previous = predicate;
                    predicate = el => previous(el) && el.GuildID == guildId && el.ChannelID == channelId;
                }

                var fromMatch = Regex.Match(ctx.ArgumentStringTrimmed, @"^from:? (\d+?)$", RegexOptions.IgnoreCase);
                if (fromMatch.Success &&
                    ulong.TryParse(fromMatch.Groups[1].Value, out ulong userId))
                {
                    var previous = predicate;
                    predicate = el => previous(el) && el.UserID == userId;
                }

                Logger.LogEvent[] logs = await logger.GetLogsAsync(after, before, predicate);

                StringBuilder sb = new StringBuilder();

                foreach (var log in logs)
                {
                    log.ToString(sb, ctx.Discord);
                    sb.Append('\n');
                }

                var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));

                if (ms.Length > 4 * 1024 * 1024)
                {
                    await ctx.ReplyAsync("Too many results, tighten the filters", mention: true);
                    return;
                }

                await ctx.Channel.SendFileAsync(ms, "Logs.txt");
            }
        }
    }
}
