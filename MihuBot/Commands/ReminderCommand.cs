using MihuBot.Reminders;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MihuBot.Commands;

public sealed partial class ReminderCommand : CommandBase
{
    public override string Command => "reminder";
    public override string[] Aliases => ["remind", "reminders", "remindme"];

    protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);
    protected override int CooldownToleranceCount => 10;

    [GeneratedRegex(@"(?:and )?(\d*(?:[\.,]\d+)?|a |an ) ?(s|sec|seconds?|m|mins?|minutes?|hr?s?|hours?|d|days?|w|weeks?|months?|y|years?)(?:[ ,]|$)",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 5_000)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"(<#?@?!?&?\d+>) ", RegexOptions.IgnoreCase)]
    private static partial Regex ReminderMentionRegex();

    private bool TryParseRemindTime(ReadOnlySpan<char> message, DateTime now, [NotNullWhen(true)] out List<(string Part, DateTime Time)> times)
    {
        times = null;

        // remind me to do stuff in an hour => [an hour]
        // remind me in 197 minutes to play starfield in the VC => [197 minutes to play starfield in the VC]
        // remind me to go play a game in a day or in a year => [a day or in a year]
        int i = message.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return false;
        }

        message = message.Slice(i + 4);

        while (!message.IsEmpty)
        {
            // [an hour] => [an hour]
            // [197 minutes to play starfield in the VC] => [197 minutes to play starfield] [the VC]
            // [a day or in a year] => [a day or] [a year]
            i = message.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);

            ReadOnlySpan<char> part;

            if (i < 0)
            {
                part = message;
                message = ReadOnlySpan<char>.Empty;
            }
            else
            {
                part = message.Slice(0, i);
                message = message.Slice(i + 4);
            }

            // [197 minutes to play starfield] => [197 minutes]
            i = part.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                part = part.Slice(0, i);
            }

            string partString = part.Trim().ToString();

            if (TryParseRemindTimeCore(partString, now, out DateTime time))
            {
                _logger.DebugLog($"Parsed '{partString}' to {time}");

                (times ??= new()).Add((partString, time));
            }
            else
            {
                _logger.DebugLog($"Failed to parse '{partString}'");
            }
        }

        return times is not null;
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
    private readonly Logger _logger;
    private readonly Timer _reminderTimer;

    public ReminderCommand(DiscordSocketClient discord, ReminderService reminderService, Logger logger)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _reminderService = reminderService ?? throw new ArgumentNullException(nameof(reminderService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        if (ctx.Content.Length <= 256 &&
            ctx.Content.Contains(" in ", StringComparison.OrdinalIgnoreCase) &&
            TryPeek(ctx) &&
            !ContainsMentionsWithoutPermissions(ctx.Message) &&
            DateTime.UtcNow is { } now &&
            TryParseRemindTime(ctx.Content.AsSpan().Trim(), now, out List<(string Part, DateTime Time)> reminderTimes) &&
            TryEnter(ctx))
        {
            if (reminderTimes.Count == 1)
            {
                var entry = new ReminderEntry(reminderTimes[0].Time, ctx.Content.Trim(), ctx);
                await Task.WhenAll(
                    ctx.Message.AddReactionAsync(Emotes.ThumbsUp),
                    _reminderService.ScheduleAsync(entry));
            }
            else
            {
                await ctx.ReplyAsync($"The reminder is ambiguous between: {string.Join(", ",
                    reminderTimes.Select(t => (t.Time - now).ToElapsedTime()))}");
            }
        }
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        if (ctx.Content.StartsWith("remind", StringComparison.OrdinalIgnoreCase))
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
