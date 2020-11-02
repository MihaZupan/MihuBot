using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using MihuBot.Reminders;
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class ReminderCommand : CommandBase
    {
        public override string Command => "reminder";
        public override string[] Aliases => new[] { "remind", "reminders", "remindme" };

        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);
        protected override int CooldownToleranceCount => 2;

        private static readonly Regex _reminderRegex = new Regex(
            @"^remind(?:ers?|me)?(?: me)? ?(?:to|that)? (.*?) ((?:in|at) (?!in|at).*?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));

        private static readonly Regex _timeRegex = new Regex(
            @"(?:and )?(\d*?(?:[\.,]\d+)?|a|an)? ?(s|sec|seconds?|m|mins?|minutes?|hr?s?|hours?|d|days?|w|weeks?|months?|y|years?)(?:[ ,]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));

        private static readonly Regex _reminderMentionRegex = new Regex(
            @"(<#?@?!?&?\d+>)(?: that)? ?(.+?)$",
            RegexOptions.IgnoreCase);

        private static bool TryParseRemindTime(string time, out DateTime dateTime)
        {
            dateTime = default;

            if (string.IsNullOrEmpty(time))
                return false;

            bool at = char.ToLowerInvariant(time[0]) == 'a';
            time = time.Trim();

            if (at)
            {
                // ToDo
            }
            else
            {
                var matches = _timeRegex.Matches(time);

                if (matches.Count > 10)
                    return false;

                var now = DateTime.UtcNow;
                dateTime = now;

                foreach (Match m in matches)
                {
                    double number = 1;
                    if (m.Groups[1].Success)
                    {
                        string quantifier = m.Groups[1].Value;

                        if (quantifier.Length == 0)
                            continue;

                        if (char.ToLowerInvariant(quantifier[0]) != 'a')
                        {
                            quantifier = quantifier.Replace(',', '.');
                            if (!double.TryParse(quantifier, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                                return false;
                        }
                    }

                    if (number > int.MaxValue)
                        return false;

                    string type = m.Groups[2].Value.ToLowerInvariant();

                    try
                    {
                        dateTime += type[0] switch
                        {
                            's' => TimeSpan.FromSeconds(number),
                            'm' when type.StartsWith("month") => FromMonths(dateTime, number),
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

                if (dateTime == now || dateTime.Year > 12_000)
                    return false;

                return true;
            }

            dateTime = default;
            return false;
        }

        private readonly DiscordSocketClient _discord;
        private readonly IReminderService _reminderService;
        private readonly Logger _logger;
        private readonly Timer _reminderTimer;

        public ReminderCommand(DiscordSocketClient discord, IReminderService reminderService, Logger logger)
        {
            _discord = discord;
            _reminderService = reminderService;
            _logger = logger;
            _reminderTimer = new Timer(_ => Task.Run(OnReminderTimerAsync), null, 1_000, Timeout.Infinite);
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            if (!ctx.Content.Contains("remind", StringComparison.OrdinalIgnoreCase)
                || !TryPeek(ctx)
                || (!ctx.IsFromAdmin && ctx.Message.MentionsAny()))
            {
                return Task.CompletedTask;
            }

            Match match;
            try
            {
                match = _reminderRegex.Match(ctx.Content);
            }
            catch (RegexMatchTimeoutException)
            {
                TryEnter(ctx);
                return Task.CompletedTask;
            }

            if (!match.Success
                || !TryParseRemindTime(match.Groups[2].Value, out DateTime reminderTime)
                || !TryEnter(ctx))
            {
                return Task.CompletedTask;
            }

            return ScheduleReminderAsync(ctx, match.Groups[1].Value, reminderTime);
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            Match match;
            try
            {
                match = _reminderRegex.Match(ctx.Content[1..]);
            }
            catch (RegexMatchTimeoutException rmte)
            {
                TryEnter(ctx);
                await ctx.ReplyAsync("Failed to process the reminder in time");
                await ctx.DebugAsync(rmte);
                return;
            }

            if (!match.Success
                || (!ctx.IsFromAdmin && ctx.Message.MentionsAny())
                || !TryParseRemindTime(match.Groups[2].Value, out DateTime reminderTime))
            {
                await ctx.ReplyAsync("Usage: `!remind me to do stuff and things in some time`");
                return;
            }

            await ScheduleReminderAsync(ctx, match.Groups[1].Value, reminderTime);
        }

        private async Task ScheduleReminderAsync(MessageContext ctx, string message, DateTime time)
        {
            message = message
                .Trim()
                .Trim(' ', '\'', '"', ',', '.', '!', '#', '?', '\r', '\n', '\t')
                .Trim();

            var entry = new ReminderEntry(time, message, ctx);

            await _reminderService.ScheduleAsync(entry);

            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
        }

        private async Task OnReminderTimerAsync()
        {
            try
            {
                foreach (ReminderEntry entry in await _reminderService.GetPendingRemindersAsync())
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Log($"Running reminder {entry}", entry);
                            var channel = _discord.GetTextChannel(entry.GuildId, entry.ChannelId);

                            Match match = _reminderMentionRegex.Match(entry.Message);

                            string message = match.Success
                                ? $"{match.Groups[1].Value} {match.Groups[2].Value}"
                                : $"{MentionUtils.MentionUser(entry.AuthorId)} {entry.Message}";

                            await channel.SendMessageAsync(message);
                        }
                        catch (Exception ex)
                        {
                            Log($"{entry} - {ex}", entry);
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
}
