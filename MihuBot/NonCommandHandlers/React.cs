namespace MihuBot.NonCommandHandlers;

public sealed class ReactCommand : NonCommandHandler
{
    private readonly DiscordSocketClient _discord;

    public ReactCommand(DiscordSocketClient discord)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        discord.ReactionAdded += Discord_ReactionAddedAsync;
    }

    private Task Discord_ReactionAddedAsync(Cacheable<IUserMessage, ulong> cacheable, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        if (Constants.Admins.Contains(reaction.UserId) &&
            DateTime.UtcNow - SnowflakeUtils.FromSnowflake(cacheable.Id) < TimeSpan.FromDays(7))
        {
            return ReactionAddedAsyncCore();
        }

        return Task.CompletedTask;

        async Task ReactionAddedAsyncCore()
        {
            try
            {
                var message = await cacheable.GetOrDownloadAsync();

                if (message != null &&
                    TryParseMessageLink(message.Content, out _, out ulong channelId, out ulong messageId))
                {
                    var linkedMessage = await _discord.GetTextChannel(channelId).GetMessageAsync(messageId);
                    await linkedMessage.AddReactionAsync(reaction.Emote);
                }
            }
            catch { }
        }
    }

    public override Task HandleAsync(MessageContext ctx) => Task.CompletedTask;

    private static bool TryParseMessageLink(string content, out ulong guildId, out ulong channelId, out ulong messageId)
    {
        guildId = channelId = messageId = 0;

        const string Prefix = "https://discord.com/channels/";

        if (!content.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = content.Substring(Prefix.Length).Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length >= 3
            && ulong.TryParse(segments[0], out guildId)
            && ulong.TryParse(segments[1], out channelId)
            && ulong.TryParse(segments[2], out messageId);
    }
}
