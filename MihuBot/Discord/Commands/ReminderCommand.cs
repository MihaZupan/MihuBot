using MihuBot.Discord;
using MihuBot.Discord.Reminders;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MihuBot.Commands;

public sealed partial class ReminderCommand : CommandBase
{
    public override string Command => "reminder";
    public override string[] Aliases => ["remind", "reminders", "remindme"];

    protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);
    protected override int CooldownToleranceCount => 10;

    private const string TimePattern = @"(?<![\w.,-])(?:and )?(\d+(?:[\.,]\d+)?|[\.,]\d+|a |an ) ?(s|sec|seconds?|m|mins?|minutes?|hr?s?|hours?|d|days?|w|weeks?|months?|y|years?)(?=[ ,.!?]|$)";

    [GeneratedRegex(TimePattern,
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 5_000)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\A" + TimePattern + @"\z",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 5_000)]
    private static partial Regex SingleTimeRegex();

    [GeneratedRegex(@"(<#?@?!?&?\d+>) ", RegexOptions.IgnoreCase)]
    private static partial Regex ReminderMentionRegex();

    [GeneratedRegex(@"\b(on|at|by|before|after|until|tomorrow|today|tonight|next|every|each|then|or|hourly|daily|weekly|monthly|yearly|annually|repeat|recurring)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CalendarTimeRegex();

    [GeneratedRegex(@"^remind(?:ers?|me)?(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReminderRequestRegex();

    internal static bool TryParseSimpleRemindTime(string message, DateTime now, out DateTime time)
    {
        time = default;

        if (CalendarTimeRegex().IsMatch(message) || TimeRegex().Count(message) != 1)
        {
            return false;
        }

        int i = message.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);

        if (i < 0 || i != message.LastIndexOf(" in ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> part = message.AsSpan(i + 4).Trim();
        i = part.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);

        if (i >= 0)
        {
            part = part.Slice(0, i);
        }

        part = part.Trim().TrimEnd(".!");
        return SingleTimeRegex().IsMatch(part) &&
            TryParseRemindTimeCore(part.ToString(), now, out time);
    }

    public static bool TryParseRemindTimeCore(string time, DateTime now, out DateTime dateTime)
    {
        dateTime = default;

        if (string.IsNullOrEmpty(time))
        {
            return false;
        }

        var matches = TimeRegex().Matches(time);

        if (matches.Count is 0 or > 10)
            return false;

        dateTime = now;

        foreach (Match m in matches)
        {
            if (!m.Groups[1].Success)
                continue;

            double number = 1;
            string quantifier = m.Groups[1].Value;

            if (quantifier.Length == 0)
                continue;

            bool isSingularQuantifier = char.ToLowerInvariant(quantifier[0]) == 'a';

            if (!isSingularQuantifier)
            {
                quantifier = quantifier.Replace(',', '.');
                if (!double.TryParse(quantifier, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                    return false;
            }

            if (number is <= 0 or > 1_000_000 or double.NaN)
                return false;

            string type = m.Groups[2].Value.ToLowerInvariant();

            if (isSingularQuantifier && type.Length < 2)
                continue; // "in a y" shouldn't be "a year"

            try
            {
                dateTime += type[0] switch
                {
                    's' => TimeSpan.FromSeconds(number),
                    'm' when type.StartsWith("month", StringComparison.Ordinal) => FromMonths(dateTime, number),
                    'm' => TimeSpan.FromMinutes(number),
                    'h' => TimeSpan.FromHours(number),
                    'd' => TimeSpan.FromDays(number),
                    'w' => TimeSpan.FromDays(number * 7),
                    'y' => FromYears(dateTime, number),
                    _ => throw new UnreachableException(type)
                };

                static TimeSpan FromMonths(DateTime now, double number) =>
                    now.AddMonths((int)number) - now + TimeSpan.FromDays(365.25d / 12d * (number % 1));

                static TimeSpan FromYears(DateTime now, double number) =>
                    now.AddYears((int)number) - now + TimeSpan.FromDays(365.25d * (number % 1));
            }
            catch { return false; }
        }

        if (dateTime <= now || dateTime.Year > 3_000)
            return false;

        return true;
    }

    private readonly DiscordSocketClient _discord;
    private readonly ReminderService _reminderService;
    private readonly ReminderRequestParser _requestParser;
    private readonly Logger _logger;
    private readonly Timer _reminderTimer;

    public ReminderCommand(DiscordSocketClient discord, ReminderService reminderService, Logger logger, ReminderRequestParser requestParser)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _reminderService = reminderService ?? throw new ArgumentNullException(nameof(reminderService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _requestParser = requestParser ?? throw new ArgumentNullException(nameof(requestParser));

        if (!OperatingSystem.IsWindows())
        {
            _reminderTimer = new Timer(_ => Task.Run(OnReminderTimerAsync), null, 1_000, Timeout.Infinite);
        }
    }

    private static bool ContainsMentionsWithoutPermissions(SocketUserMessage message)
    {
        if (message.Guild() is SocketGuild guild &&
            guild.GetUser(message.Author.Id) is SocketGuildUser user &&
            user.GuildPermissions.MentionEveryone)
        {
            return false;
        }

        return message.MentionedEveryone || message.MentionedRoles.Count != 0;
    }

    private async Task ExecuteAsyncCore(MessageContext ctx)
    {
        if (ctx.Content.Length > 256 ||
            !TryPeek(ctx) ||
            ContainsMentionsWithoutPermissions(ctx.Message) ||
            !TryEnter(ctx))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool simple = TryParseSimpleRemindTime(ctx.Content, now, out DateTime time);

        if (!simple)
        {
            ReminderParseResult result;

            try
            {
                result = await _requestParser.ParseAsync(ctx.Guild.Id, ctx.AuthorId, ctx.Content, now, ctx.CancellationToken);
            }
            catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.DebugLog(ex, "Failed to interpret reminder");
                await ctx.ReplyAsync("Sorry, I couldn't interpret that reminder right now. Please try again.", suppressMentions: true);
                return;
            }

            if (result.Time is not { } parsedTime)
            {
                await ctx.ReplyAsync(result.Error, suppressMentions: true);
                return;
            }

            time = parsedTime;
        }

        if (time <= DateTime.UtcNow)
        {
            await ctx.ReplyAsync("That reminder time has already passed. Please specify a future time.");
            return;
        }

        var entry = new ReminderEntry(time, ctx.Content.Trim(), ctx);
        await _reminderService.ScheduleAsync(entry);
        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);

        if (!simple)
        {
            long timestamp = new DateTimeOffset(time).ToUnixTimeSeconds();
            await ctx.ReplyAsync($"Reminder set for <t:{timestamp}:F> (<t:{timestamp}:R>).", suppressMentions: true);
        }
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        if (ReminderRequestRegex().IsMatch(ctx.Content))
        {
            return ExecuteAsyncCore(ctx);
        }

        return Task.CompletedTask;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        await ExecuteAsyncCore(ctx);
    }

    private async Task OnReminderTimerAsync()
    {
        try
        {
            ICollection<ReminderEntry> reminders = await _reminderService.GetPendingRemindersAsync();

            if (reminders.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (ReminderEntry entry in reminders)
                    {
                        try
                        {
                            Log($"Running reminder {entry}", entry);
                            var channel = _discord.GetTextChannel(entry.ChannelId);

                            if (channel is null)
                            {
                                Log($"Failed to find the channel for {entry}", entry);
                                continue;
                            }

                            Match match = ReminderMentionRegex().Match(entry.Message);

                            string mention = match.Success ? match.Groups[1].Value : MentionUtils.MentionUser((ulong)entry.AuthorId);

                            IMessage message = entry.MessageId == 0 ? null : await channel.GetMessageAsync(entry.MessageId);

                            if (message is not null)
                            {
                                if (message.Reactions.Any(r => r.Key.Name == Emotes.ThumbsUp.Name && r.Value.ReactionCount > 1 && r.Value.ReactionCount <= 10))
                                {
                                    var reactions = await message.GetReactionUsersAsync(Emotes.ThumbsUp, 15).ToArrayAsync();
                                    var users = reactions
                                        .SelectMany(r => r)
                                        .Where(r => !r.IsBot && r.Id != (ulong)entry.AuthorId)
                                        .Where(r => !mention.Contains(r.Id.ToString(), StringComparison.Ordinal));
                                    string extraMentions = string.Join(' ', users.Select(u => MentionUtils.MentionUser(u.Id)));
                                    if (extraMentions.Length != 0)
                                    {
                                        mention = $"{mention} {extraMentions}";
                                    }
                                }

                                await channel.SendMessageAsync(
                                    mention,
                                    messageReference: new MessageReference(message.Id, entry.ChannelId, entry.GuildId));
                            }
                            else
                            {
                                string reminderMessage = match.Success ? match.Groups[2].Value : entry.Message;
                                await channel.SendMessageAsync($"{mention} {reminderMessage}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"{entry} - {ex}", entry);
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.DebugLog(ex.ToString());
        }
        finally
        {
            _reminderTimer.Change(1_000, Timeout.Infinite);
        }
    }

    private void Log(string message, ReminderEntry entry) =>
        _logger.DebugLog(message, guildId: entry.GuildId, channelId: entry.ChannelId, userId: (ulong)entry.AuthorId);
}
