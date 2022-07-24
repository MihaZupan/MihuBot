using System.Text.Json;
using System.Text.RegularExpressions;

namespace MihuBot.Commands;

public sealed class LogsCommand : CommandBase
{
    public override string Command => "logs";
    public override string[] Aliases => new[] { "plogs", "pvtlogs", "privatelogs", "ddlogs" };

    private readonly Logger _logger;
    private readonly CustomLogger[] _customLoggers;

    public LogsCommand(Logger logger, IEnumerable<CustomLogger> customLogger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _customLoggers = customLogger.ToArray();
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!await ctx.RequirePermissionAsync("logs"))
            return;

        if (ctx.Command != Command && ctx.AuthorId != KnownUsers.Miha)
            return;

        Logger logger = _logger;
        if (ctx.Command != Command)
        {
            logger = ctx.Command == "ddlogs"
                ? (_customLoggers.FirstOrDefault(l => l is DDsLogger))?.Logger
                : (_customLoggers.FirstOrDefault(l => l is PrivateLogger))?.Logger;
        }

        if (logger is null)
            return;

        if (ctx.ArgumentLines.Length == 0)
        {
            await ctx.ReplyAsync("Add filters", mention: true);
            return;
        }

        if (ctx.ArgumentLines.Length == 1 && ctx.ArgumentLines[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            await logger.ResetLogFileAsync();
            return;
        }

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
        HashSet<Logger.EventType> typeFilters = null;
        var containsFilters = new List<string>();
        var regexFilters = new List<Regex>();

        var after = new DateTime(2000, 1, 1);
        var before = new DateTime(3000, 1, 1);

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

            var typeMatch = Regex.Match(line, @"^type:? (.*?)$", RegexOptions.IgnoreCase);
            if (typeMatch.Success)
            {
                var typeRegex = new Regex(typeMatch.Groups[1].Value, RegexOptions.IgnoreCase);
                typeFilters = Enum.GetValues<Logger.EventType>()
                    .Where(et => typeRegex.IsMatch(et.ToString()))
                    .ToHashSet();
                continue;
            }

            var containsMatch = Regex.Match(line, @"^contains:? (.*?)$", RegexOptions.IgnoreCase);
            if (containsMatch.Success)
            {
                containsFilters.Add(containsMatch.Groups[1].Value);
                continue;
            }

            var regexMatch = Regex.Match(line, @"^(?:regex|match|matches):? (.*?)$", RegexOptions.IgnoreCase);
            if (regexMatch.Success)
            {
                regexFilters.Add(new Regex(regexMatch.Groups[1].Value, RegexOptions.IgnoreCase | RegexOptions.Compiled));
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

        if (typeFilters != null)
        {
            predicates.Add(le => typeFilters.Contains(le.Type));
        }

        if (ctx.AuthorId != KnownUsers.Miha)
        {
            HashSet<ulong> channels = ctx.Author.MutualGuilds
                .SelectMany(g => g.Channels)
                .Where(c => c.HasReadAccess(ctx.AuthorId))
                .Select(c => c.Id)
                .ToHashSet();
            predicates.Add(le => le.ChannelID == 0 || channels.Contains(le.ChannelID));
        }

        if (regexFilters.Any())
        {
            var tempSb = new StringBuilder();
            var filters = regexFilters.ToArray();

            predicates.Add(le =>
            {
                tempSb.Length = 0;
                le.ToString(tempSb, ctx.Discord);
                string toString = tempSb.ToString();

                foreach (var regex in filters)
                {
                    if (!regex.IsMatch(toString))
                    {
                        return false;
                    }
                }
                return true;
            });
        }

        RosBytePredicate rawJsonPredicate = null;
        if (containsFilters.Any())
        {
            byte[][] containsBytesFilters = containsFilters
                .Select(cf => Encoding.UTF8.GetBytes(cf))
                .ToArray();

            rawJsonPredicate = rawJson =>
            {
                foreach (byte[] containsBytesFilter in containsBytesFilters)
                {
                    if (rawJson.IndexOf(containsBytesFilter) == -1)
                    {
                        return false;
                    }
                }
                return true;
            };
        };

        (Logger.LogEvent[] logs, Exception[] errors) = await logger.GetLogsAsync(after, before, predicates.ToArray().All, rawJsonPredicate);

        if (errors.Length > 0)
        {
            string separator = new('-', 50);

            StringBuilder errorBuilder = new();
            foreach (var byErrorMessage in errors.GroupBy(e => e.StackTrace))
            {
                errorBuilder.AppendLine(separator);
                errorBuilder.AppendLine(byErrorMessage.Key);
                foreach (var subGroup in byErrorMessage.GroupBy(e => e.InnerException?.Message ?? e.Message))
                {
                    errorBuilder.AppendLine();
                    errorBuilder.AppendLine(subGroup.Key);
                }
            }

            await ctx.Channel.SendTextFileAsync("ParsingErrors.txt", errorBuilder.ToString());
        }

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

        StringBuilder sb = new();

        foreach (var log in logs)
        {
            if (raw)
            {
                sb.Append(JsonSerializer.Serialize(log, Logger.JsonOptions));
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
