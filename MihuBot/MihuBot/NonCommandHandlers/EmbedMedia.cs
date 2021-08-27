using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
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

        private readonly HttpClient _http;

        public EmbedMedia(HttpClient httpClient)
        {
            _http = httpClient;
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
                    if (message.Content.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) &&
                        message.Content.Contains("http", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = _tiktokRegex.Match(message.Content);

                        if (!match.Success)
                            continue;

                        try
                        {
                            YoutubeDlMetadata metadata = await YoutubeDl.GetJsonAsync<YoutubeDlMetadata>(match.Value);
                            await UploadFileAsync(ctx, metadata);
                        }
                        catch (Exception ex)
                        {
                            await ctx.DebugAsync(ex, message.Content);
                            await ctx.Message.AddReactionAsync(Emotes.RedCross);
                        }

                        break;
                    }
                }
            }
        }

        private async Task UploadFileAsync(MessageContext ctx, YoutubeDlMetadata metadata)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, metadata.Url);
            foreach (var header in metadata.HttpHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using Stream responseStream = await response.Content.ReadAsStreamAsync();

            await ctx.Channel.SendFileAsync(responseStream, metadata.Filename);
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy), ItemNullValueHandling = NullValueHandling.Ignore)]
        private class YoutubeDlMetadata
        {
            public string Url { get; set; }
            public Dictionary<string, string> HttpHeaders { get; set; }

            [JsonProperty(PropertyName = "_filename")]
            public string Filename { get; set; }
        }
    }
}
