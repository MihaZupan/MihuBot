using Discord.Rest;

namespace MihuBot
{
    public class MessageContext
    {
        private readonly Logger _logger;

        public readonly DiscordSocketClient Discord;

        public readonly SocketUserMessage Message;
        public readonly string Content;

        public bool IsMentioned
        {
            get
            {
                if (Message.MentionedUsers.Any(BotId))
                {
                    return true;
                }

                string name = BotUser.Username;
                foreach (SocketRole role in Message.MentionedRoles)
                {
                    if (role.Name == name)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public MessageContext(DiscordSocketClient discord, SocketUserMessage message, Logger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Discord = discord ?? throw new ArgumentNullException(nameof(discord));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Content = message.Content.Trim();
        }

        public SocketTextChannel Channel => (SocketTextChannel)Message.Channel;
        public SocketGuild Guild => Message.Guild();

        public bool IsFromAdmin => Constants.Admins.Contains(AuthorId);

        public SocketGuildUser Author => (SocketGuildUser)Message.Author;
        public ulong AuthorId => Message.Author.Id;

        public ulong BotId => Discord.CurrentUser.Id;
        public SocketGuildUser BotUser => Guild.GetUser(BotId);
        public GuildPermissions GuildPermissions => BotUser.GuildPermissions;
        public ChannelPermissions ChannelPermissions => BotUser.GetPermissions(Channel);

        public async Task<RestUserMessage> ReplyAsync(string text, bool mention = false, bool suppressMentions = false)
        {
            if (mention)
                text = MentionUtils.MentionUser(AuthorId) + " " + text;

            if (!ChannelPermissions.SendMessages)
            {
                throw new Exception($"Missing permissions to send `{text}` in {Channel.Name}.");
            }

            AllowedMentions mentions = suppressMentions ? AllowedMentions.None : null;
            try
            {
                return await Channel.SendMessageAsync(text, allowedMentions: mentions);
            }
            catch (HttpRequestException hre) when (hre.InnerException is IOException)
            {
                await Task.Delay(100);

                return await Channel.SendMessageAsync(text, allowedMentions: mentions);
            }
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
