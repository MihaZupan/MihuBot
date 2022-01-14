using MihuBot.Reminders;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MihuBot.Commands
{
    public sealed class ReminderCommand : CommandBase
    {
        public override string Command => "reminder";
        public override string[] Aliases => new[] { "remind", "reminders", "remindme" };

        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);
        protected override int CooldownToleranceCount => 10;

        private static readonly Regex _reminderRegex = new Regex(
            @"^remind(?:ers?|me)?(?: me)? ?(?:to|that)? (.*?) ((?:in|at) (?!in|at).*?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));

        private static readonly Regex _timeRegex = new Regex(
            @"(?:and )?(\d*?(?:[\.,]\d+)?|a|an)? ?(s|sec|seconds?|m|mins?|minutes?|hr?s?|hours?|d|days?|w|weeks?|months?|y|years?)(?:[ ,]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));

        private static readonly Regex _reminderMentionRegex = new Regex(
            @"(<#?@?!?&?\d+>) ?(?:to|that)? ?(.+?)$",
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

        public override Task HandleAsync(MessageContext ctx)
        {
            if (!ctx.Content.Contains("remind", StringComparison.OrdinalIgnoreCase)
                || ctx.Content.Length > 256
                || !TryPeek(ctx)
                || ContainsMentionsWithoutPermissions(ctx.Message))
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
            await ctx.ReplyAsync("Usage: `remind me to slap Joster in 2 minutes`");
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

                                Match match = _reminderMentionRegex.Match(entry.Message);

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
}
