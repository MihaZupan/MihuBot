using MihuBot.API;
using MihuBot.Configuration;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace MihuBot;

public sealed class TelegramService
{
    private readonly TelegramBotClient _telegram;
    private readonly InitializedDiscordClient _discord;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;

    private IUserMessage _lastLocationUpdateMessage;

    public TelegramService(TelegramBotClient telegram, Logger logger, IConfigurationService configuration)
    {
        _telegram = telegram;
        _logger = logger;
        _discord = _logger.Options.Discord;
        _configuration = configuration;

        _ = Task.Run(StartAsync);
    }

    private async Task StartAsync()
    {
        await _discord.WaitUntilInitializedAsync();

        _discord.MessageReceived += message => OnMessageCreatedOrEditedAsync(message, update: false);
        _discord.MessageUpdated += (_, message, _) => OnMessageCreatedOrEditedAsync(message, update: true);

        if (Debugger.IsAttached)
        {
            // Disabled to prevent a debug session from breaking the webhook.
            //_ = Task.Run(async () =>
            //{
            //    await _telegram.ReceiveAsync((_, update, _) => HandleUpdateAsync(update), (_, _, _) => Task.CompletedTask);
            //}, CancellationToken.None);
        }
        else
        {
            try
            {
                await _telegram.SetWebhookAsync(TelegramBotController.WebhookPath, secretToken: TelegramBotController.WebhookUpdateSecret);
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync($"Failed to setup telegram bot webhook: {ex}");
            }
        }
    }

    private async Task OnMessageCreatedOrEditedAsync(SocketMessage message, bool update)
    {
        if (message.Author.IsBot ||
            message.Channel.Id is not (Channels.PrivateGeneral or Channels.TheBoysTgRelay))
        {
            return;
        }

        _logger.DebugLog("Relaying Discord message to Telegram", message as SocketUserMessage);

        try
        {
            string author = message.Author.Username;
            string edited = update ? " (edited)" : "";

            if (message.Attachments.FirstOrDefault() is { } attachment)
            {
                string extension = Path.GetExtension(attachment.Filename)?.ToLowerInvariant();
                string caption = $"{author}{edited}: {message.Content} {attachment.Description}";

                if (extension is ".jpg" or ".png")
                {
                    await _telegram.SendPhotoAsync(Constants.MihuTelegramId, InputFile.FromUri(attachment.Url), caption: caption);
                }
                else
                {
                    await _telegram.SendDocumentAsync(Constants.MihuTelegramId, InputFile.FromUri(attachment.Url), caption: caption);
                }
            }
            else if (message.Content is string text && !string.IsNullOrEmpty(text))
            {
                await _telegram.SendTextMessageAsync(Constants.MihuTelegramId, $"{author}{edited}: {text}");
            }
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"Failed to send message to telegram: {ex}");
        }
    }

    public async Task HandleUpdateAsync(Update update)
    {
        try
        {
            Message message = update.Message ?? update.EditedMessage;

            if (message is not null)
            {
                if (DateTime.UtcNow.Subtract(message.EditDate ?? message.Date) > TimeSpan.FromMinutes(1))
                {
                    return;
                }

                if (message.Chat.Id != Constants.MihuTelegramId ||
                    message.From?.Id != Constants.MihuTelegramId)
                {
                    return;
                }

                if (await TryHandleMessageAsync(message))
                {
                    await _telegram.SendTextMessageAsync(message.Chat.Id, "Relayed");
                }
            }
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"Failed to handle telegram update: {ex}");
        }
    }

    private async Task<bool> TryHandleMessageAsync(Message message)
    {
        _logger.DebugLog("Relaying Telegram message to Discord");

        ulong channelId = Debugger.IsAttached ? Channels.PrivateGeneral : Channels.TheBoysTgRelay;

        if (_configuration.TryGet(null, "TelegramService.DiscordChannel", out string channelIdString) &&
            ulong.TryParse(channelIdString, out ulong channelIdFromConfig))
        {
            channelId = channelIdFromConfig;
        }

        var channel = _discord.GetTextChannel(channelId);

        if (message.Text is string text)
        {
            await channel.TrySendMessageAsync($"Mihu via Telegram: {text}", logger: _logger);
        }
        else if (message.Photo is { } photo)
        {
            var bestPhoto = photo.MaxBy(p => p.Width * p.Height);

            await RelayFileAsync(channel, bestPhoto, message.Caption, "jpg", "jpg");
        }
        else if (message.Video is { } video)
        {
            await RelayFileAsync(channel, video, message.Caption, Path.GetExtension(video.FileName)?.TrimStart('.') ?? "mp4", "mp4");
        }
        else if (message.Audio is { } audio)
        {
            await RelayFileAsync(channel, audio, message.Caption, Path.GetExtension(audio.FileName)?.TrimStart('.') ?? "mp3", null, audio.FileName);
        }
        else if (message.Voice is { } voice)
        {
            await RelayFileAsync(channel, voice, message.Caption, "mp3", "mp3");
        }
        else if (message.Document is { } document)
        {
            await RelayFileAsync(channel, document, message.Caption, Path.GetExtension(document.FileName)?.TrimStart('.') ?? "bin", null, document.FileName);
        }
        else if (message.Location is { } location)
        {
            string lat = location.Latitude.ToString(CultureInfo.InvariantCulture);
            string lon = location.Longitude.ToString(CultureInfo.InvariantCulture);
            float? uncertainty = location.HorizontalAccuracy;

            string newMessage = $"Mihu via Telegram: [Location](https://www.google.com/maps/place/{lat},{lon})";

            if (uncertainty is not null && uncertainty > 1)
            {
                newMessage += $" +/- {(int)uncertainty.Value} meters";
            }

            if (_lastLocationUpdateMessage is not null &&
                _lastLocationUpdateMessage.Channel.Id == channel.Id &&
                DateTime.UtcNow.Subtract(_lastLocationUpdateMessage.Timestamp.UtcDateTime) < TimeSpan.FromDays(1) &&
                channel.GetCachedMessages(limit: 100).Any(m => m.Id == _lastLocationUpdateMessage.Id))
            {
                await _lastLocationUpdateMessage.ModifyAsync(m => m.Content = newMessage);
                return false;
            }
            else
            {
                _lastLocationUpdateMessage = await channel.TrySendMessageAsync(newMessage, logger: _logger);
            }
        }
        else
        {
            return false;
        }

        return true;
    }

    private async Task RelayFileAsync(ITextChannel channel, FileBase telegramFile, string caption, string sourceExt, string destinationExt, string fileName = null)
    {
        using var tempSrcFile = new TempFile(sourceExt);
        using var tempDstFile = new TempFile(destinationExt ?? "");

        using (var tempFileWriteFs = System.IO.File.OpenWrite(tempSrcFile.Path))
        {
            await _telegram.GetInfoAndDownloadFileAsync(telegramFile.FileId, tempFileWriteFs);
        }

        string destinationPath = tempSrcFile.Path;

        if (destinationExt is not null)
        {
            await YoutubeHelper.FFMpegConvertAsync(tempSrcFile.Path, tempDstFile.Path, "");
            destinationPath = tempDstFile.Path;
        }

        destinationExt ??= sourceExt;
        fileName ??= $"tg-{telegramFile.FileUniqueId}.{destinationExt}";

        await channel.TrySendFilesAsync([new FileAttachment(destinationPath, fileName)], $"Mihu via Telegram: {caption}", _logger);
    }
}
