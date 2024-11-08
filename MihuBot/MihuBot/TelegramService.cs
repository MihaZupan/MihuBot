using MihuBot.API;
using MihuBot.Configuration;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace MihuBot;

public sealed class TelegramService
{
    private readonly TelegramBotClient _telegram;
    private readonly HttpClient _http;
    private readonly InitializedDiscordClient _discord;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;
    private readonly string _googleMapsApiKey;

    private IUserMessage _lastLocationUpdateMessage;
    private List<(DateTime Timestamp, string Lat, string Lon)> _locationHistory = new();

    public TelegramService(TelegramBotClient telegram, HttpClient http, Logger logger, IConfigurationService configurationService, IConfiguration configuration)
    {
        _telegram = telegram;
        _http = http;
        _logger = logger;
        _discord = _logger.Options.Discord;
        _configuration = configurationService;
        _googleMapsApiKey = configuration["GoogleMaps:ApiKey"];

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
            _locationHistory.RemoveAll(l => DateTime.UtcNow.Subtract(l.Timestamp) > TimeSpan.FromMinutes(15));

            string lat = location.Latitude.ToString(CultureInfo.InvariantCulture);
            string lon = location.Longitude.ToString(CultureInfo.InvariantCulture);
            double? uncertainty = location.HorizontalAccuracy;

            _locationHistory.Add((DateTime.UtcNow, lat, lon));

            string newMessage = $"Mihu via Telegram: [Location](https://www.google.com/maps/place/{lat},{lon})";

            if (uncertainty is not null && uncertainty > 1)
            {
                newMessage += $" +/- {(int)uncertainty.Value} meters";
            }

            string googleMapsImageLink = "https://maps.googleapis.com/maps/api/staticmap";
            googleMapsImageLink += $"?key={_googleMapsApiKey}";
            googleMapsImageLink += "&size=480x270";
            googleMapsImageLink += "&format=png";
            googleMapsImageLink += "&language=en";
            googleMapsImageLink += $"&markers={lat},{lon}";

            if (_locationHistory.Count > 1)
            {
                googleMapsImageLink += $"&path={string.Join('|', _locationHistory.Select(l => $"{l.Lat},{l.Lon}"))}";

                var prevLocation = _locationHistory[^2];
                var currLocation = _locationHistory[^1];

                double lastDistanceMeters = GetDistanceMeters(
                    double.Parse(prevLocation.Lon, CultureInfo.InvariantCulture),
                    double.Parse(prevLocation.Lat, CultureInfo.InvariantCulture),
                    double.Parse(currLocation.Lon, CultureInfo.InvariantCulture),
                    double.Parse(currLocation.Lat, CultureInfo.InvariantCulture));

                double timeDeltaHours = (currLocation.Timestamp - prevLocation.Timestamp).TotalHours;

                double speedKmph = lastDistanceMeters / 1000 / timeDeltaHours;

                newMessage += $" ({speedKmph.ToString("F1", CultureInfo.InvariantCulture)} km/h)";
            }

            using TempFile tempImageFile = new("png");

            await using (var tempImageFsWriteStream = System.IO.File.Create(tempImageFile.Path))
            {
                using Stream imageStream = await _http.GetStreamAsync(googleMapsImageLink);
                await imageStream.CopyToAsync(tempImageFsWriteStream);
            }

            using var attachment = new FileAttachment(tempImageFile.Path, $"TelegramLocation-{DateTime.UtcNow.ToISODateTime()}.png");

            if (_lastLocationUpdateMessage is not null &&
                _lastLocationUpdateMessage.Channel.Id == channel.Id &&
                DateTime.UtcNow.Subtract(_lastLocationUpdateMessage.Timestamp.UtcDateTime) < TimeSpan.FromDays(1) &&
                channel.GetCachedMessages(limit: 100).Any(m => m.Id == _lastLocationUpdateMessage.Id))
            {
                await _lastLocationUpdateMessage.ModifyAsync(m =>
                {
                    m.Content = newMessage;
                    m.Attachments = new[] { attachment };
                });
                return false;
            }
            else
            {
                _lastLocationUpdateMessage = await channel.TrySendFilesAsync([attachment], newMessage, logger: _logger);
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

        await using (var tempFileWriteFs = System.IO.File.OpenWrite(tempSrcFile.Path))
        {
            await _telegram.GetInfoAndDownloadFile(telegramFile.FileId, tempFileWriteFs);
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

    // https://stackoverflow.com/a/51839058/6845657
    private static double GetDistanceMeters(double longitude, double latitude, double otherLongitude, double otherLatitude)
    {
        var d1 = latitude * (Math.PI / 180.0);
        var num1 = longitude * (Math.PI / 180.0);
        var d2 = otherLatitude * (Math.PI / 180.0);
        var num2 = otherLongitude * (Math.PI / 180.0) - num1;
        var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) + Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);

        return 6376500.0 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3)));
    }
}
