﻿using MihuBot.Reminders;
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

    private static bool TryParseRemindTime(ReadOnlySpan<char> message, [NotNullWhen(true)] out List<(string Part, DateTime Time)> times)
    {
        times = null;

        // remind me to do stuff in an hour => [an hour]
        // remind me in 197 minutes to play starfield in the VC => [197 minutes to play starfield in the VC]
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

            part = part.Trim();

            // [197 minutes to play starfield] => [197 minutes]
            i = part.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                part = part.Slice(0, i).Trim();
            }

            string partString = part.ToString();

            if (TryParseRemindTimeCore(partString, out DateTime time))
            {
                (times ??= new()).Add((partString, time));
                return true;
            }
        }

        return times is not null;
    }

    private static bool TryParseRemindTimeCore(string time, out DateTime dateTime)
    {
        dateTime = default;

        if (string.IsNullOrEmpty(time))
        {
            return false;
        }

        var matches = TimeRegex().Matches(time);

        if (matches.Count is 0 or > 10)
            return false;

        var now = DateTime.UtcNow;
        dateTime = now;

        foreach (Match m in matches)
        {
            if (!m.Groups[1].Success)
                return false;

            double number = 1;
            string quantifier = m.Groups[1].Value;

            if (quantifier.Length == 0)
                return false;

            if (char.ToLowerInvariant(quantifier[0]) != 'a')
            {
                quantifier = quantifier.Replace(',', '.');
                if (!double.TryParse(quantifier, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                    return false;
            }

            if (number is <= 0 or > 1_000_000 or double.NaN)
                return false;

            string type = m.Groups[2].Value.ToLowerInvariant();

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
                    _ => TimeSpan.Zero
                };

                static TimeSpan FromMonths(DateTime now, double number) =>
                    now.AddMonths((int)number) - now + TimeSpan.FromDays((365.25d / 12d) * (number % 1));

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
        _reminderTimer = new Timer(_ => Task.Run(OnReminderTimerAsync), null, 1_000, Timeout.Infinite);
    }

    private static bool ContainsMentionsWithoutPermissions(SocketUserMessage message)
    {
        if (message.Guild() is SocketGuild guild &&
            guild.GetUser(message.Author.Id) is SocketGuildUser user &&
            user.GuildPermissions.MentionEveryone)
        {
            return false;
        }

        return message.MentionedEveryone || message.MentionedRoles.Any();
    }

    public override async Task HandleAsync(MessageContext ctx)
    {
        if (ctx.Content.StartsWith("remind", StringComparison.OrdinalIgnoreCase) &&
            ctx.Content.Length <= 256 &&
            ctx.Content.Contains(" in ", StringComparison.OrdinalIgnoreCase) &&
            TryPeek(ctx) &&
            !ContainsMentionsWithoutPermissions(ctx.Message) &&
            TryParseRemindTime(ctx.Content.AsSpan().Trim(), out List<(string Part, DateTime Time)> reminderTimes) &&
            TryEnter(ctx))
        {
            if (reminderTimes.Count == 1)
            {
                var entry = new ReminderEntry(reminderTimes[0].Time, ctx.Content.Trim(), ctx);
                await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                await _reminderService.ScheduleAsync(entry);
            }
            else
            {
                await ctx.ReplyAsync($"The reminder is ambiguous between: {string.Join(", ",
                    reminderTimes.Select(t => (t.Time - DateTime.UtcNow).ToElapsedTime()))}");
            }
        }
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        await ctx.ReplyAsync("Usage: `remind me to slap Joster in 2 minutes`");
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

                            string mention = match.Success ? match.Groups[1].Value : MentionUtils.MentionUser(entry.AuthorId);

                            IMessage message = entry.MessageId == 0 ? null : await channel.GetMessageAsync(entry.MessageId);

                            if (message is not null)
                            {
                                if (message.Reactions.Any(r => r.Key.Name == Emotes.ThumbsUp.Name && r.Value.ReactionCount > 1 && r.Value.ReactionCount <= 10))
                                {
                                    var reactions = await message.GetReactionUsersAsync(Emotes.ThumbsUp, 15).ToArrayAsync();
                                    var users = reactions
                                        .SelectMany(r => r)
                                        .Where(r => !r.IsBot && r.Id != entry.AuthorId)
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
        _logger.DebugLog(message, guildId: entry.GuildId, channelId: entry.ChannelId, authorId: entry.AuthorId);
}
