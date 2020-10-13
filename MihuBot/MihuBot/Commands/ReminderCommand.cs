using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using MihuBot.Reminders;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class ReminderCommand : CommandBase
    {
        public override string Command => "reminder";
        public override string[] Aliases => new[] { "remind", "remindme" };

        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);
        protected override int CooldownToleranceCount => 2;

        private static readonly Regex _reminderRegex = new Regex(
            @"^remind(?:er|me)?(?: me)? ?(?:to|that)? (.*?) ((?:in|at) (?!in|at).*?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));

        private static readonly Regex _timeRegex = new Regex(
            @"(\d+|a|an)? ?(s|sec|seconds?|m|min|minutes?|hr?s?|hours?|d|days?|w|weeks?|months?|y|years?)(?: |$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));

        private static bool TryParseRemindTime(string time, out DateTime dateTime)
        {
            bool at = char.ToLowerInvariant(time[0]) == 'a';
            time = time.Trim();

            if (at)
            {
                // ToDo
            }
            else
            {
                dateTime = default;
                var matches = _timeRegex.Matches(time);

                if (matches.Count > 10)
                    return false;

                var now = DateTime.UtcNow;
                dateTime = now;

                foreach (Match m in matches)
                {
                    int number = 1;
                    if (m.Groups[1].Success)
                    {
                        string quantifier = m.Groups[1].Value;
                        if (char.ToLowerInvariant(quantifier[0]) != 'a')
                        {
                            if (!int.TryParse(quantifier, out number))
                                return false;
                        }
                    }

                    string type = m.Groups[2].Value.ToLowerInvariant();

                    if (number > 1_000_000 && type[0] != 's')
                        return false;

                    TimeSpan segment = type[0] switch
                    {
                        's' => TimeSpan.FromSeconds(number),
                        'm' when type.StartsWith("month") => now.AddMonths(number) - now,
                        'm' => TimeSpan.FromMinutes(number),
                        'h' => TimeSpan.FromHours(number),
                        'd' => TimeSpan.FromDays(number),
                        'w' => TimeSpan.FromDays(number * 7),
                        'y' => now.AddYears(number) - now,
                        _ => TimeSpan.Zero
                    };

                    if (segment == TimeSpan.Zero)
                        return false;

                    dateTime += segment;
                }

                if (dateTime != now)
                    return true;
            }

            dateTime = default;
            return false;
        }

        private readonly DiscordSocketClient _discord;
        private readonly IReminderService _reminderService;
        private readonly Timer _reminderTimer;

        public ReminderCommand(DiscordSocketClient discord, IReminderService reminderService)
        {
            _discord = discord;
            _reminderService = reminderService;
            _reminderTimer = new Timer(_ => Task.Run(OnReminderTimerAsync), null, 1_000, Timeout.Infinite);
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            if (!ctx.Content.Contains("remind", StringComparison.OrdinalIgnoreCase)
                || !TryPeek(ctx)
                || (!ctx.IsFromAdmin &&
                    (ctx.Message.MentionedChannels.Any()
                    || ctx.Message.MentionedRoles.Any()
                    || ctx.Message.MentionedUsers.Any(u => u.Id != KnownUsers.MihuBot))))
            {
                return Task.CompletedTask;
            }

            string content = ctx.Content;

            if (ctx.IsMentioned)
            {
                if (!content.StartsWith("@MihuBot", StringComparison.OrdinalIgnoreCase))
                    return Task.CompletedTask;

                content = content.Substring("@MihuBot".Length);
            }

            Match match;
            try
            {
                match = _reminderRegex.Match(content.Trim());
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
                await ctx.DebugAsync(rmte.ToString());
                return;
            }

            if (!match.Success
                || (!ctx.IsFromAdmin && (ctx.Message.MentionedChannels.Any() || ctx.Message.MentionedRoles.Any() || ctx.Message.MentionedUsers.Any()))
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
                            Logger.DebugLog($"Running reminder {entry}");
                            var channel = _discord.GetTextChannel(entry.GuildId, entry.ChannelId);
                            await channel.SendMessageAsync($"{MentionUtils.MentionUser(entry.AuthorId)} {entry.Message}");
                        }
                        catch (Exception ex)
                        {
                            Logger.DebugLog($"{entry} - {ex}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.DebugLog(ex.ToString());
            }
            finally
            {
                _reminderTimer.Change(1_000, Timeout.Infinite);
            }
        }
    }
}
