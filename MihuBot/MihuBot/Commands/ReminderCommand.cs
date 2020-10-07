using MihuBot.Helpers;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class ReminderCommand : CommandBase
    {
        public override string Command => "reminder";
        public override string[] Aliases => new[] { "remind", "remindme" };

        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);
        protected override int CooldownToleranceCount => 5;

        private static readonly Regex _reminderRegex = new Regex(
            @"^remind(?:er|me)?(?: me)?(?: to)? (.*?) ((?:in|at) (?!in|at).*?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

        private static bool TryParseRemindTime(string time, out TimeSpan timeSpan)
        {
            timeSpan = default;
            return false;
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            if (!ctx.Content.Contains("remind", StringComparison.OrdinalIgnoreCase)
                || !TryPeek(ctx))
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
                || !TryParseRemindTime(match.Groups[2].Value, out TimeSpan reminderTime)
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
                || !TryParseRemindTime(match.Groups[2].Value, out TimeSpan reminderTime))
            {
                await ctx.ReplyAsync("Usage: `!remind me to do stuff and things in some time`");
                return;
            }

            await ScheduleReminderAsync(ctx, match.Groups[1].Value, reminderTime);
        }

        private static async Task ScheduleReminderAsync(MessageContext ctx, string message, TimeSpan time)
        {
            message = message.Trim();


            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
        }
    }
}
