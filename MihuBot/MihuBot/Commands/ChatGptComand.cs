using MihuBot.Configuration;
using Newtonsoft.Json.Linq;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace MihuBot.Commands
{
    public sealed class ChatGptComand : CommandBase
    {
        public override string Command => "chatgpt";
        public override string[] Aliases => new[] { "gpt" };

        private readonly Logger _logger;
        private readonly HttpClient _http;
        private readonly IConfigurationService _configurationService;
        private readonly string _apiKey;
        private readonly string[] _commandAndAliases;

        public ChatGptComand(Logger logger, HttpClient http, IConfiguration configuration, IConfigurationService configurationService)
        {
            _logger = logger;
            _http = http;
            _configurationService = configurationService;
            _apiKey = configuration["ChatGPT:Key"];
            _commandAndAliases = Enumerable.Concat(Aliases, new string[] { Command }).ToArray();
        }

        private async Task<string> QueryCompletionsAsync(string model, string prompt, int maxTokens, ulong authorId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/completions");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = JsonContent.Create(new
            {
                model,
                prompt,
                max_tokens = maxTokens,
                user = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"Discord_{authorId}")))
            });

            using HttpResponseMessage response = await _http.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            _logger.DebugLog($"ChatGPT response for {authorId} with model={model} maxTokens={maxTokens} prompt='{prompt}' was '{responseJson}'");

            string text = JToken.Parse(responseJson)["choices"].AsJEnumerable().First()["text"].ToObject<string>();

            text = text.ReplaceLineEndings("\n").Trim('\n');

            return text;
        }

        public override Task ExecuteAsync(CommandContext ctx)
        {
            return HandleAsync(ctx.Channel, ctx.AuthorId, ctx.ArgumentStringTrimmed);
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            string content = ctx.Content;

            foreach (string command in _commandAndAliases)
            {
                if (content.StartsWith(command, StringComparison.OrdinalIgnoreCase))
                {
                    return HandleAsync(ctx.Channel, ctx.AuthorId, content.Substring(command.Length).Trim());
                }
            }

            return Task.CompletedTask;
        }

        private async Task HandleAsync(SocketTextChannel channel, ulong authorId, string prompt)
        {
            if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.Model", out string model))
            {
                model = "text-davinci-003";
            }

            if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.MaxTokens", out string maxTokensString) ||
                !uint.TryParse(maxTokensString, out uint maxTokens) ||
                maxTokens > 2048)
            {
                maxTokens = 200;
            }

            string response = await QueryCompletionsAsync(model, prompt, (int)maxTokens, authorId);

            await channel.SendMessageAsync(response);
        }
    }
}
