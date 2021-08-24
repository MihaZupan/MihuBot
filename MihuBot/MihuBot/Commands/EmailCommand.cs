using Markdig;
using MihuBot.Email;
using MihuBot.Helpers;
using System.Text.RegularExpressions;

namespace MihuBot.Commands
{
    public sealed class EmailCommand : CommandBase
    {
        public override string Command => "email";

        private readonly IEmailService _emailService;

        public EmailCommand(IEmailService emailService)
        {
            _emailService = emailService;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync("email.send"))
                return;

            const string Usage = "Usage:\n```\n!email FirstName LastName Someone@domain.tld Subject line\nContent\n```";

            string[] lines = ctx.ArgumentLines;
            if (lines.Length < 2)
            {
                await ctx.ReplyAsync(Usage, mention: true);
                return;
            }

            Match match = Regex.Match(lines[0], @"(\S*?(?: \S*?)?) (\S+?@\S+?\.\S+?) (.*?)$");
            if (!match.Success)
            {
                await ctx.ReplyAsync(Usage, mention: true);
                return;
            }

            string toName = match.Groups[1].Value;
            string toEmail = match.Groups[2].Value;
            string subject = match.Groups[3].Value;

            string plainText = string.Join('\n', lines.Skip(1));
            string html = Markdown.ToHtml(plainText);

            await _emailService.SendEmailAsync("MihuBot", "MihuBot", toName, toEmail, subject, plainText, html);
            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
        }
    }
}
