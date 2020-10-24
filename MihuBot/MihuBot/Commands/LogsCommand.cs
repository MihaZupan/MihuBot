using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class LogsCommand : CommandBase
    {
        public override string Command => "logs";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions() { IgnoreNullValues = true };

        private readonly Logger _logger;

        public LogsCommand(Logger logger)
        {
            _logger = logger;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            bool reset = ctx.ArgumentLines.Length == 1 && ctx.ArgumentLines[0].Equals("reset", StringComparison.OrdinalIgnoreCase);

            if (ctx.ArgumentLines.Length == 0 || reset)
            {
                await _logger.SendLogFilesAsync(_logger.LogsReportsTextChannel, resetLogFiles: reset);
            }
            else
            {
                var predicates = new List<Predicate<Logger.LogEvent>>();

                if (!ctx.ArgumentLines[0].Contains("all", StringComparison.OrdinalIgnoreCase))
                {
                    predicates.Add(el =>
                        el.Type == Logger.EventType.MessageReceived ||
                        el.Type == Logger.EventType.MessageUpdated ||
                        el.Type == Logger.EventType.MessageDeleted ||
                        el.Type == Logger.EventType.FileReceived);
                }

                bool raw = ctx.ArgumentLines[0].Contains("raw", StringComparison.OrdinalIgnoreCase);

                bool afterSet = false, beforeSet = false, lastSet = false;

                var fromFilters = new List<ulong>();
                var inFilters = new List<ulong>();

                DateTime after = new DateTime(2000, 1, 1);
                DateTime before = new DateTime(3000, 1, 1);

                foreach (string line in ctx.ArgumentLines)
                {
                    if (TryParseBeforeAfter(line, out bool isBefore, out DateTime time))
                    {
                        if (lastSet || (beforeSet && isBefore) || (afterSet && !isBefore))
                        {
                            await ctx.ReplyAsync("Only use one 'after', 'before' or 'last' filter");
                            return;
                        }

                        if (isBefore)
                        {
                            beforeSet = true;
                            before = time;
                        }
                        else
                        {
                            afterSet = true;
                            after = time;
                        }

                        continue;
                    }

                    var lastMatch = Regex.Match(line, @"^last:? (\d*?)? ?(s|sec|seconds?|m|mins?|minutes?|hr?s?|hours?|d|days?|w|weeks?)$", RegexOptions.IgnoreCase);
                    if (lastMatch.Success)
                    {
                        lastSet = true;
                        var groups = lastMatch.Groups;

                        ulong number = 1;
                        if (groups[1].Success && (!ulong.TryParse(groups[1].Value, out number) || number > 100_000))
                        {
                            await ctx.ReplyAsync("Please use a reasonable number", mention: true);
                            return;
                        }

                        TimeSpan length = char.ToLowerInvariant(groups[2].Value[0]) switch
                        {
                            's' => TimeSpan.FromSeconds(number),
                            'm' => TimeSpan.FromMinutes(number),
                            'h' => TimeSpan.FromHours(number),
                            'd' => TimeSpan.FromDays(number),
                            'w' => TimeSpan.FromDays(number * 7),
                            _ => throw new ArgumentException("Unknown time format")
                        };

                        after = DateTime.UtcNow.Subtract(length);
                        continue;
                    }


                    var fromMatch = Regex.Match(line, @"^from:? ((?:\d+? ?)+)$", RegexOptions.IgnoreCase);
                    if (fromMatch.Success)
                    {
                        foreach (string from in fromMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (ulong.TryParse(from, out ulong userId))
                            {
                                fromFilters.Add(userId);
                            }
                        }
                        continue;
                    }

                    var inMatch = Regex.Match(line, @"^in:? (\d+?)$", RegexOptions.IgnoreCase);
                    if (inMatch.Success && ulong.TryParse(inMatch.Groups[1].Value, out ulong id))
                    {
                        inFilters.Add(id);
                        continue;
                    }
                }

                if (after >= before)
                {
                    await ctx.ReplyAsync("'After' must be earlier in time than 'Before'", mention: true);
                    return;
                }

                if (fromFilters.Count != 0)
                {
                    predicates.Add(le => fromFilters.Contains(le.UserID));
                }

                if (inFilters.Count != 0)
                {
                    predicates.Add(le => inFilters.Contains(le.GuildID) || inFilters.Contains(le.ChannelID));
                }

                Logger.LogEvent[] logs = await _logger.GetLogsAsync(after, before, predicates.ToArray().All);

                if (logs.Length == 0)
                {
                    await ctx.ReplyAsync("No results, consider relaxing the filters", mention: true);
                    return;
                }

                if (logs.Length > 10_000)
                {
                    await ctx.ReplyAsync("Too many results, tighten the filters", mention: true);
                    return;
                }

                StringBuilder sb = new StringBuilder();

                foreach (var log in logs)
                {
                    if (raw)
                    {
                        sb.Append(JsonSerializer.Serialize(log, JsonOptions));
                    }
                    else
                    {
                        log.ToString(sb, ctx.Discord);
                    }
                    sb.Append('\n');
                }

                var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));

                if (stream.Length > 4 * 1024 * 1024)
                {
                    await ctx.ReplyAsync("Too many results, tighten the filters", mention: true);
                    return;
                }

                await ctx.Channel.SendFileAsync(stream, "Logs.txt");
            }
        }

        private static bool TryParseBeforeAfter(string line, out bool isBefore, out DateTime time)
        {
            isBefore = default;
            time = default;

            var match = Regex.Match(line, @"^(before|after):? (\d\d\d\d)[^\d](\d\d?)[^\d](\d\d?)(?:[^\d](\d\d?)[^\d](\d\d?)[^\d](\d\d?))?$", RegexOptions.IgnoreCase);

            if (!match.Success)
                return false;

            var groups = match.Groups;

            isBefore = groups[1].Value.Equals("before", StringComparison.OrdinalIgnoreCase);

            time = new DateTime(int.Parse(groups[2].Value), int.Parse(groups[3].Value), int.Parse(groups[4].Value));
            if (groups[5].Success)
            {
                time = time.AddHours(int.Parse(groups[5].Value)).AddMinutes(int.Parse(groups[6].Value)).AddSeconds(int.Parse(groups[7].Value));
            }
            return true;
        }
    }
}
