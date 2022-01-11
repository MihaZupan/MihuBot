using System.Text.RegularExpressions;

namespace MihuBot.NonCommandHandlers
{
    public sealed class EmbedMedia : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        protected override int CooldownToleranceCount => 10;

        private static readonly Regex _tiktokRegex = new(
            @"https?:\/\/.*?tiktok\.com\/(?:@[^\/]+\/video\/\d+|\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            matchTimeout: TimeSpan.FromSeconds(5));

        private static readonly Regex _instagramReelRegex = new(
            @"https?:\/\/.*?instagram\.com\/reel\/\w+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            matchTimeout: TimeSpan.FromSeconds(5));

        private readonly HttpClient _http;

        public EmbedMedia(HttpClient httpClient)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.Equals("embed", StringComparison.OrdinalIgnoreCase) &&
                TryEnter(ctx) &&
                ctx.ChannelPermissions.AttachFiles)
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                await Task.Yield();

                var history = ctx.Channel.GetCachedMessages(limit: 5);

                foreach (SocketMessage message in history)
                {
                    if (!message.Content.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
                        !message.Content.Contains("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (message.Content.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = _tiktokRegex.Match(message.Content);
                        if (match.Success)
                        {
                            await TryExtractAndUploadVideoAsync(ctx, match.Value, message.Content);
                            break;
                        }
                    }
                    else if (message.Content.Contains("instagram.com/reel/", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = _instagramReelRegex.Match(message.Content);
                        if (match.Success)
                        {
                            await TryExtractAndUploadVideoAsync(ctx, match.Value, message.Content);
                            break;
                        }
                    }
                }
            }
        }

        private async Task TryExtractAndUploadVideoAsync(MessageContext ctx, string url, string content)
        {
            try
            {
                await UploadFileAsync(ctx, await YoutubeDl.GetMetadataAsync(url));
            }
            catch (Exception ex)
            {
                await ctx.DebugAsync(ex, content);
                await ctx.Message.AddReactionAsync(Emotes.RedCross);
            }
        }

        private async Task UploadFileAsync(MessageContext ctx, YoutubeDl.YoutubeDlMetadata metadata)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, metadata.Url);
            foreach (var header in metadata.HttpHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using Stream responseStream = await response.Content.ReadAsStreamAsync();

            using var tempFile = new TempFile("mp4");

            using (var writeFs = File.OpenWrite(tempFile.Path))
            {
                await responseStream.CopyToAsync(writeFs);
            }

            using (var readFs = File.OpenRead(tempFile.Path))
            {
                await ctx.Channel.SendFileAsync(readFs, metadata.Filename);
            }
        }
    }
}
