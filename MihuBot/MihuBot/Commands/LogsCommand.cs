using System.Buffers;
using System.Text.RegularExpressions;

namespace MihuBot.Commands;

public sealed partial class LogsCommand : CommandBase
{
    public override string Command => "logs";

    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(10);
    protected override int CooldownToleranceCount => 50;

    private readonly Logger _logger;

    public LogsCommand(Logger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.ArgumentLines.Length == 0)
        {
            await ctx.ReplyAsync("Add filters", mention: true);
            return;
        }

        var filters = new List<Func<IQueryable<LogDbEntry>, IQueryable<LogDbEntry>>>();
        var postFilters = new List<Func<IEnumerable<LogDbEntry>, IEnumerable<LogDbEntry>>>();

        if (!ctx.HasPermission("logs") || !ctx.ArgumentLines[0].Contains("all", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add(q => q.Where(log =>
                log.Type == Logger.EventType.MessageReceived ||
                log.Type == Logger.EventType.MessageUpdated ||
                log.Type == Logger.EventType.MessageDeleted ||
                log.Type == Logger.EventType.FileReceived));
        }

        bool raw = ctx.ArgumentLines[0].Contains("raw", StringComparison.OrdinalIgnoreCase);

        bool afterSet = false, beforeSet = false, lastSet = false;

        var fromFilters = new List<ulong>();
        var inFilters = new List<ulong>();
        HashSet<Logger.EventType> typeFilters = null;
        var containsFilters = new List<string>();
        var regexFilters = new List<Regex>();

        var after = new DateTime(2000, 1, 1);
        var before = DateTime.UtcNow.Add(TimeSpan.FromDays(366));

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

            var lastMatch = LastTimeRegex().Match(line);
            if (lastMatch.Success)
            {
                lastSet = true;
                string lastTime = lastMatch.Groups[1].Value;

                var now = DateTime.UtcNow;
                if (!ReminderCommand.TryParseRemindTimeCore(lastTime, now, out DateTime remindTime))
                {
                    await ctx.ReplyAsync($"Failed to parse '{lastTime}'", mention: true);
                    return;
                }

                TimeSpan duration = remindTime - now;
                after = now.Subtract(duration);
                continue;
            }


            var fromMatch = FromRegex().Match(line);
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

            var inMatch = InRegex().Match(line);
            if (inMatch.Success && ulong.TryParse(inMatch.Groups[1].Value, out ulong id))
            {
                inFilters.Add(id);
                continue;
            }

            var typeMatch = TypeRegex().Match(line);
            if (typeMatch.Success)
            {
                var typeRegex = new Regex(typeMatch.Groups[1].Value, RegexOptions.IgnoreCase);
                typeFilters = Enum.GetValues<Logger.EventType>()
                    .Where(et => typeRegex.IsMatch(et.ToString()))
                    .ToHashSet();
                continue;
            }

            var containsMatch = ContainsRegex().Match(line);
            if (containsMatch.Success)
            {
                containsFilters.Add(containsMatch.Groups[1].Value);
                continue;
            }

            var regexMatch = MatchesRegex().Match(line);
            if (regexMatch.Success)
            {
                regexFilters.Add(new Regex(regexMatch.Groups[1].Value, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)));
                continue;
            }
        }

        if (after >= before || after.Year is not (> 2010 and <= 2070) || before.Year is not (> 2010 and <= 2070))
        {
            await ctx.ReplyAsync("'After' must be earlier in time than 'Before'", mention: true);
            return;
        }

        if (fromFilters.Count != 0)
        {
            if (fromFilters.Count == 1)
            {
                filters.Add(q => q.Where(log => log.UserId == (long)fromFilters[0]));
            }
            else
            {
                postFilters.Add(q => q.Where(log => fromFilters.Contains((ulong)log.UserId)));
            }
        }

        if (inFilters.Count != 0)
        {
            if (inFilters.Count == 1)
            {
                ulong from = inFilters[0];
                filters.Add(q => q.Where(log => log.GuildId == (long)from || log.ChannelId == (long)from));
            }
            else
            {
                postFilters.Add(q => q.Where(log => inFilters.Contains((ulong)log.GuildId) || inFilters.Contains((ulong)log.ChannelId)));
            }
        }

        if (typeFilters != null)
        {
            if (typeFilters.Count == 1)
            {
                filters.Add(q => q.Where(log => log.Type == typeFilters.First()));
            }
            else
            {
                postFilters.Add(q => q.Where(log => typeFilters.Contains(log.Type)));
            }
        }

        if (ctx.AuthorId != KnownUsers.Miha)
        {
            postFilters.Add(q => q.Where(log =>
                ctx.Discord.GetGuild((ulong)log.GuildId) is { } guild &&
                guild.GetUser(ctx.AuthorId) is { } authorGuildUser &&
                authorGuildUser.JoinedAt is { } joinedAt &&
                log.Timestamp >= joinedAt &&
                guild.GetChannel((ulong)log.ChannelId) is { } channel &&
                channel.HasReadAccess(ctx.AuthorId)));
        }

        if (containsFilters.Count != 0 || regexFilters.Count != 0)
        {
            var containsValues = containsFilters
                .Select(c => SearchValues.Create([c], StringComparison.OrdinalIgnoreCase))
                .ToArray();

            postFilters.Add(q => q.Where(log =>
            {
                string json = log.AsJson();

                foreach (var value in containsValues)
                {
                    if (!json.AsSpan().ContainsAny(value))
                    {
                        return false;
                    }
                }

                foreach (var regex in regexFilters)
                {
                    if (!regex.IsMatch(json))
                    {
                        return false;
                    }
                }

                return true;
            }));
        };

        int maxResults = ctx.IsFromAdmin ? 100_000 : 10_000;
        int maxPostFilterExecutions = ctx.IsFromAdmin ? 50_000_000 : 1_000_000;

        postFilters.Add(q => q.Take(maxResults + 1));
        filters.Add(q => q.Take(maxPostFilterExecutions + 1));

        int postFiltersExecuted = 0;
        postFilters.Insert(0, q => q.Where(_ =>
        {
            postFiltersExecuted++;
            return true;
        }));

        using var queryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Stopwatch stopwatch = Stopwatch.StartNew();

        LogDbEntry[] logs;
        try
        {
            logs = await _logger.GetLogsAsync(after, before,
                query: query =>
                {
                    foreach (var filter in filters)
                    {
                        query = filter(query);
                    }

                    return query;
                },
                filters: enumerable =>
                {
                    foreach (var filter in postFilters)
                    {
                        enumerable = filter(enumerable);
                    }

                    return enumerable;
                },
                cancellationToken: queryCts.Token);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException || queryCts.IsCancellationRequested)
        {
            await ctx.ReplyAsync("The query ran for too long", mention: true);
            return;
        }

        stopwatch.Stop();

        ctx.DebugLog($"Got {logs.Length} results with {postFiltersExecuted} postFilter executions in {stopwatch.ElapsedMilliseconds:N2} ms");

        if (logs.Length == 0)
        {
            await ctx.ReplyAsync("No results, consider relaxing the filters", mention: true);
            return;
        }

        if (logs.Length > maxResults)
        {
            await ctx.ReplyAsync("Too many results, tighten the filters", mention: true);
            return;
        }

        if (postFiltersExecuted > maxPostFilterExecutions)
        {
            await ctx.ReplyAsync("Query too broad, tighten the filters", mention: true);
            return;
        }

        StringBuilder sb = new();

        foreach (var log in logs)
        {
            if (raw)
            {
                sb.Append(log.AsJson());
            }
            else
            {
                log.ToString(sb, ctx.Discord);
            }
            sb.Append('\n');

            if (sb.Length > 4 * 1024 * 1024)
            {
                await ctx.ReplyAsync("Too many results, tighten the filters", mention: true);
                return;
            }
        }

        await ctx.Channel.SendTextFileAsync($"Logs-{Snowflake.NextString()}.txt", sb.ToString());
    }

    private static bool TryParseBeforeAfter(string line, out bool isBefore, out DateTime time)
    {
        isBefore = default;
        time = default;

        var match = BeforeAfterRegex().Match(line);

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

    [GeneratedRegex(@"^(before|after):? (\d\d\d\d)[^\d](\d\d?)[^\d](\d\d?)(?:[^\d](\d\d?)[^\d](\d\d?)[^\d](\d\d?))?$", RegexOptions.IgnoreCase)]
    private static partial Regex BeforeAfterRegex();

    [GeneratedRegex(@"^from:? ((?:\d+? ?)+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FromRegex();

    [GeneratedRegex(@"^in:? (\d+?)$", RegexOptions.IgnoreCase)]
    private static partial Regex InRegex();

    [GeneratedRegex(@"^type:? (.*?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"^contains:? (.*?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainsRegex();

    [GeneratedRegex(@"^(?:regex|match|matches):? (.*?)$", RegexOptions.IgnoreCase)]
    private static partial Regex MatchesRegex();

    [GeneratedRegex(@"^last:? (.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LastTimeRegex();
}
