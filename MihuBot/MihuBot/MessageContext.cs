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
        public readonly DiscordSocketClient Discord;

        public readonly SocketUserMessage Message;
        public readonly string Content;

        public readonly bool IsMentioned;
        public readonly bool IsFromAdmin;

        public MessageContext(DiscordSocketClient discord, SocketUserMessage message)
        {
            Discord = discord;
            Message = message;
            Content = message.Content.Trim();
            IsMentioned = message.MentionedUsers.Any(u => u.Id == KnownUsers.MihuBot) || message.MentionedRoles.Any(r => r.Name == "MihuBot");
            IsFromAdmin = message.AuthorIsAdmin();
        }

        public ISocketMessageChannel Channel => Message.Channel;
        public SocketGuild Guild => Message.Guild();

        public SocketUser Author => Message.Author;
        public ulong AuthorId => Author.Id;

        public async Task<RestUserMessage> ReplyAsync(string text, bool mention = false)
        {
            if (mention)
                text = MentionUtils.MentionUser(AuthorId) + " " + text;

            return await Channel.SendMessageAsync(text);
        }

        public async Task WarnCooldownAsync(TimeSpan cooldown)
        {
            int seconds = (int)Math.Ceiling(cooldown.TotalSeconds);
            await ReplyAsync($"Please wait at least {seconds} more second{(seconds == 1 ? "" : "s")}", mention: true);
        }

        internal async Task DebugAsync(string debugMessage) => await Logger.Instance.DebugAsync(debugMessage, Message);

        internal void DebugLog(string debugMessage) => Logger.DebugLog(debugMessage, Message);
    }
}
