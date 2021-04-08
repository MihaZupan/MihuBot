﻿using Discord;
using Discord.Rest;
using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Diagnostics;
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

        public bool IsMentioned => Message.MentionedUsers.Any(KnownUsers.MihuBot) || Message.MentionedRoles.Any(r => r.Name == "MihuBot");

        public MessageContext(DiscordSocketClient discord, SocketUserMessage message, Logger logger)
        {
            _logger = logger;
            Discord = discord;
            Message = message;
            Content = message.Content.Trim();
        }

        public SocketTextChannel Channel => (SocketTextChannel)Message.Channel;
        public SocketGuild Guild => Message.Guild();

        public bool IsFromAdmin => Constants.Admins.Contains(AuthorId);

        public SocketGuildUser Author => (SocketGuildUser)Message.Author;
        public ulong AuthorId => Author.Id;

        public SocketGuildUser BotUser => Guild.GetUser(Discord.CurrentUser.Id);

        public async Task<RestUserMessage> ReplyAsync(string text, bool mention = false, bool suppressMentions = false)
        {
            if (mention)
                text = MentionUtils.MentionUser(AuthorId) + " " + text;

            if (!BotUser.GetPermissions(Channel).SendMessages)
            {
                throw new Exception($"Missing permissions to send `{text}` in {Channel.Name}.");
            }

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
