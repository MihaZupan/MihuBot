using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using SharpCollections.Generic;
using System;
using System.Collections.Generic;
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
            @"^remind(?:er|me)?(?: me)?(?: to)? (.*?) ((?:in|at) (?!in|at).*?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

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
                var matches = Regex.Matches(time, @"(\d*?)? ?(s|sec|seconds?|m|min|minutes|h|hours?|d|days?|w|weeks?|months?|y|years?)(?: |$)");

                if (matches.Count > 10)
                    return false;

                var now = DateTime.UtcNow;
                dateTime = now;

                foreach (Match m in matches)
                {
                    if (!int.TryParse(m.Groups[1].Value, out int number))
                        return false;

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

                if (dateTime != default)
                    return true;
            }

            dateTime = default;
            return false;
        }

        public override async Task InitAsync(ServiceCollection services)
        {
            List<ReminderEntry> reminders = await _reminders.EnterAsync();
            try
            {
                lock (_remindersHeap)
                {
                    foreach (var entry in reminders)
                        _remindersHeap.Push(entry);
                }
            }
            catch
            {
                _reminders.Exit();
            }

            _reminderTimer = new Timer(
                client => OnReminderTimer(client as DiscordSocketClient),
                services.Discord,
                1_000,
                Timeout.Infinite);
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


        private class ReminderEntry : IComparable<ReminderEntry>
        {
            public DateTime Time { get; set; }
            public string Message { get; set; }
            public ulong GuildId { get; set; }
            public ulong ChannelId { get; set; }
            public ulong AuthorId { get; set; }

            public ReminderEntry(DateTime time, string message, MessageContext ctx)
            {
                Time = time;
                Message = message;
                GuildId = ctx.Guild.Id;
                ChannelId = ctx.Channel.Id;
                AuthorId = ctx.AuthorId;
            }

            public int CompareTo(ReminderEntry other) => Time.CompareTo(other.Time);

            public override string ToString()
            {
                return $"{Time} {GuildId}-{ChannelId}-{AuthorId}: {Message}";
            }
        }

        private Timer _reminderTimer;

        private readonly BinaryHeap<ReminderEntry> _remindersHeap =
            new BinaryHeap<ReminderEntry>(32);

        private static readonly SynchronizedLocalJsonStore<List<ReminderEntry>> _reminders =
            new SynchronizedLocalJsonStore<List<ReminderEntry>>("Reminders.json");

        private async Task ScheduleReminderAsync(MessageContext ctx, string message, DateTime time)
        {
            message = message.Trim();
            var entry = new ReminderEntry(time, message, ctx);

            await ctx.Services.Logger.DebugAsync($"Setting reminder entry for {entry}", logOnly: true);

            List<ReminderEntry> reminders = await _reminders.EnterAsync();
            try
            {
                reminders.Add(entry);
                lock (_remindersHeap)
                {
                    _remindersHeap.Push(entry);
                }
            }
            finally
            {
                _reminders.Exit();
            }

            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
        }

        private void OnReminderTimer(DiscordSocketClient client)
        {
            var now = DateTime.UtcNow;
            List<ReminderEntry> entries = null;

            lock (_remindersHeap)
            {
                while (!_remindersHeap.IsEmpty && _remindersHeap.Top.Time >= now)
                {
                    (entries ??= new List<ReminderEntry>()).Add(_remindersHeap.Pop());
                }
            }

            if (entries != null)
            {
                List<ReminderEntry> reminders = _reminders.EnterAsync().GetAwaiter().GetResult();
                try
                {
                    foreach (var entry in entries)
                        reminders.Remove(entry);
                }
                finally
                {
                    _reminders.Exit();
                }

                foreach (var entry in entries.Where(e => e.Time - now < TimeSpan.FromSeconds(10)))
                {
                    Logger.Instance.DebugAsync($"Running reminder {entry}", logOnly: true).GetAwaiter().GetResult();

                    Task.Run(async () =>
                    {
                        try
                        {
                            var channel = client.GetTextChannel(entry.GuildId, entry.ChannelId);
                            await channel.SendMessageAsync($"{MentionUtils.MentionUser(entry.AuthorId)} {entry.Message}");
                        }
                        catch (Exception ex)
                        {
                            await Logger.Instance.DebugAsync($"{entry} - {ex}");
                        }
                    });
                }
            }

            _reminderTimer.Change(1_000, Timeout.Infinite);
        }
    }
}
