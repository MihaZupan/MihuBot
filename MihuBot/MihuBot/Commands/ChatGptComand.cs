using MihuBot.Configuration;
using Newtonsoft.Json.Linq;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Commands;

public sealed class ChatGptComand : CommandBase
{
    private const string JaredCommand = "askjared";

    private static readonly IEmote[] JaredEmotes = new[]
    {
        new Emoji("🥛"),
        new Emoji("👶"),
        new Emoji("🍼"),
        new Emoji("💩"),
    };

    public override string Command => "chatgpt";
    public override string[] Aliases => new[] { "gpt", JaredCommand };

    private readonly Logger _logger;
    private readonly HttpClient _http;
    private readonly IConfigurationService _configurationService;
    private readonly string _apiKey;
    private readonly string[] _commandAndAliases;
    private readonly Dictionary<ulong, List<HistoryEntry>> _chatHistory = new();

    public ChatGptComand(Logger logger, HttpClient http, IConfiguration configuration, IConfigurationService configurationService)
    {
        _logger = logger;
        _http = http;
        _configurationService = configurationService;
        _apiKey = configuration["ChatGPT:Key"];
        _commandAndAliases = Enumerable.Concat(Aliases, new string[] { Command }).ToArray();
    }

    private record HistoryEntry(string Role, string Content);

    private async Task<string> QueryCompletionsAsync(SocketTextChannel channel, ulong authorId, string systemPrompt, string prompt, string model, int maxTokens, int maxChatHistory)
    {
        bool useChatHistory = maxChatHistory > 1;

        string uri = useChatHistory
            ? "https://api.openai.com/v1/chat/completions"
            : "https://api.openai.com/v1/completions";

        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        string userHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"Discord_{channel.Id}_{authorId}")));

        if (useChatHistory)
        {
            List<HistoryEntry> history = null;

            lock (_chatHistory)
            {
                if (_chatHistory.TryGetValue(channel.Id, out var historyList))
                {
                    history = historyList.ToList();
                }
            }

            history ??= new();
            history.Add(new HistoryEntry("user", prompt));

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                history.Insert(0, new HistoryEntry("system", systemPrompt));
            }

            request.Content = JsonContent.Create(new
            {
                model,
                messages = history,
                max_tokens = maxTokens,
                user = userHash
            });
        }
        else
        {
            request.Content = JsonContent.Create(new
            {
                model,
                prompt,
                max_tokens = maxTokens,
                user = userHash
            });
        }

        using HttpResponseMessage response = await _http.SendAsync(request);
        string responseJson = await response.Content.ReadAsStringAsync();

        _logger.DebugLog($"ChatGPT response for {authorId} in channel={channel.Id} with model={model} maxTokens={maxTokens} prompt='{prompt}' was '{responseJson}'");

        JToken choice = JToken.Parse(responseJson)["choices"].AsJEnumerable().First();

        string text = useChatHistory
            ? choice["message"]["content"].ToObject<string>()
            : choice["text"].ToObject<string>();

        text = text
            .ReplaceLineEndings("\n")
            .TrimStart('.', ',', '?', '!', ':', '\n', ' ')
            .TrimEnd('\n', ' ');

        if (useChatHistory)
        {
            lock (_chatHistory)
            {
                var history = CollectionsMarshal.GetValueRefOrAddDefault(_chatHistory, channel.Id, out _) ??= new();

                history.Add(new HistoryEntry("user", prompt));
                history.Add(new HistoryEntry("assistant", text));

                if (history.Count > maxChatHistory)
                {
                    history.RemoveRange(0, history.Count - maxChatHistory);
                }
            }
        }

        return text;
    }

    public override Task ExecuteAsync(CommandContext ctx)
    {
        return HandleAsync(ctx.Channel, ctx.AuthorId, ctx.Command, ctx.ArgumentStringTrimmed);
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        string content = ctx.Content;

        foreach (string command in _commandAndAliases)
        {
            if (content.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                return HandleAsync(ctx.Channel, ctx.AuthorId, command, content.Substring(command.Length).Trim());
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketTextChannel channel, ulong authorId, string command, string prompt)
    {
        bool isJared = command.Equals(JaredCommand, StringComparison.OrdinalIgnoreCase);

        if (isJared)
        {
            int chance = _configurationService.TryGet(channel.Guild.Id, "ChatGPT.JaredBadResponseChance", out string chanceString) && int.TryParse(chanceString, out int chanceValue)
                ? chanceValue
                : 10;

            if (Rng.Chance(chance))
            {
                await channel.SendMessageAsync(JaredEmotes.Random().Name);
                return;
            }
        }

        if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.Model", out string model))
        {
            model = "text-davinci-003";
        }

        if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.ChatModel", out string chatModel))
        {
            chatModel = "gpt-4";
        }

        if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.MaxTokens", out string maxTokensString) ||
            !uint.TryParse(maxTokensString, out uint maxTokens) ||
            maxTokens > 2048)
        {
            maxTokens = 200;
        }

        if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.MaxChatHistory", out string maxChatHistoryString) ||
            !uint.TryParse(maxChatHistoryString, out uint maxChatHistory) ||
            maxChatHistory > 1000)
        {
            maxChatHistory = 20;
        }

        if (!_configurationService.TryGet(channel.Guild.Id, $"ChatGPT.SystemPrompt{(isJared ? ".Jared" : "")}", out string systemPrompt))
        {
            if (isJared)
            {
                systemPrompt = Rng.Bool()
                    ? "Your name is Jared who speaks a bit funny."
                    : "Your name is Jared who likes to turn everything into a joke.";
            }
            else
            {
                systemPrompt = "You are a helpful assistant named MihuBot.";
            }
        }

        string response = await QueryCompletionsAsync(channel, authorId, systemPrompt, prompt, maxChatHistory > 1 ? chatModel : model, (int)maxTokens, (int)maxChatHistory);

        await channel.SendMessageAsync(response);
    }
}
