using Discord;
using Discord.Rest;
using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot
{
    public class MessageContext
    {
        private readonly Logger _logger;

        public readonly DiscordSocketClient Discord;

        public readonly SocketUserMessage Message;
        public readonly string Content;

        public readonly bool IsMentioned;

        public MessageContext(DiscordSocketClient discord, SocketUserMessage message, Logger logger)
        {
            _logger = logger;
            Discord = discord;
            Message = message;
            Content = message.Content.Trim();
            IsMentioned = message.MentionedUsers.Any(u => u.Id == KnownUsers.MihuBot) || message.MentionedRoles.Any(r => r.Name == "MihuBot");
        }

        public SocketTextChannel Channel => (SocketTextChannel)Message.Channel;
        public SocketGuild Guild => Message.Guild();

        public bool IsFromAdmin => Constants.Admins.Contains(AuthorId);

        public SocketGuildUser Author => (SocketGuildUser)Message.Author;
        public ulong AuthorId => Author.Id;

        public async Task<RestUserMessage> ReplyAsync(string text, bool mention = false, bool suppressMentions = false)
        {
            if (mention)
                text = MentionUtils.MentionUser(AuthorId) + " " + text;

            return await Channel.SendMessageAsync(text, allowedMentions: suppressMentions ? AllowedMentions.None : null);
        }

        public async Task WarnCooldownAsync(TimeSpan cooldown)
        {
            int seconds = (int)Math.Ceiling(cooldown.TotalSeconds);
            await ReplyAsync($"Please wait at least {seconds} more second{(seconds == 1 ? "" : "s")}", mention: true);
        }

        internal async Task DebugAsync(string debugMessage) => await _logger.DebugAsync(debugMessage, Message);

        internal Task DebugAsync(Exception ex, string extraDebugInfo = "") =>
            DebugAsync($"{Guild.Id}-{Channel.Id}-{Message.Id}-{AuthorId} ({Author.Username}#{Author.DiscriminatorValue}) {extraDebugInfo}: {ex} for -- {Content}");

        internal void DebugLog(string debugMessage) => _logger.DebugLog(debugMessage, Message);

        internal void DebugLog(Exception ex, string extraDebugInfo = "") =>
            DebugLog($"{Guild.Id}-{Channel.Id}-{Message.Id}-{AuthorId} ({Author.Username}#{Author.DiscriminatorValue}) {extraDebugInfo}: {ex} for -- {Content}");
    }
}
