using MihuBot.Configuration;

namespace MihuBot.NonCommandHandlers;

public sealed class SecretSanta : NonCommandHandler
{
    private readonly InitializedDiscordClient _discord;
    private readonly IConfigurationService _configuration;
    private readonly HttpClient _http;
    private readonly Logger _logger;

    public SecretSanta(InitializedDiscordClient discord, IConfigurationService configuration, HttpClient http, Logger logger)
    {
        _discord = discord;
        _configuration = configuration;
        _http = http;
        _logger = logger;

        _discord.MessageReceived += HandleMessageAsync;
    }

    public override Task HandleAsync(MessageContext ctx) => Task.CompletedTask;

    private async Task HandleMessageAsync(SocketMessage message)
    {
        try
        {
            if (message.Channel is IDMChannel &&
                _configuration.TryGet(null, "SecretSantaRole", out string roleIdString) &&
                ulong.TryParse(roleIdString, out ulong roleId) &&
                _configuration.TryGet(null, "SecretSantaChannel", out string channelIdString) &&
                ulong.TryParse(channelIdString, out ulong channelId) &&
                (_discord.GetGuild(Guilds.TheBoys)?.GetUser(message.Author.Id)?.Roles?.Any(r => r.Id == roleId) ?? false) &&
                _discord.GetTextChannel(channelId) is { } channel &&
                _configuration.TryGet(null, "SecretSantaPrefix", out string messagePrefix) &&
                (!string.IsNullOrWhiteSpace(message.Content) || message.Attachments.Count != 0))
            {
                if (!string.IsNullOrEmpty(messagePrefix)) messagePrefix += " ";
                string content = $"{messagePrefix}{message.Content}";

                Attachment[] sourceAttachments = message.Attachments.ToArray();

                if (sourceAttachments.Length != 0)
                {
                    FileAttachment[] attachments = new FileAttachment[sourceAttachments.Length];
                    await Parallel.ForAsync(0, attachments.Length, async (i, ct) =>
                    {
                        Attachment source = sourceAttachments[i];
                        Stream s = await _http.GetStreamAsync(source.Url, ct);
                        attachments[i] = new FileAttachment(s, source.Filename, source.Description, source.IsSpoiler());
                    });

                    try
                    {
                        await channel.SendFilesAsync(attachments, content);
                    }
                    finally
                    {
                        foreach (var attachment in attachments)
                        {
                            await attachment.Stream.DisposeAsync();
                        }
                    }
                }
                else
                {
                    await channel.SendMessageAsync(content);
                }
            }
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync(nameof(HandleMessageAsync), ex, message as SocketUserMessage);
        }
    }
}
