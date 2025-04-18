﻿using Discord.Rest;
using MihuBot.Configuration;
using OpenAI.Chat;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Commands;

public sealed class ChatGptComand : CommandBase
{
    private const string JaredCommand = "askjared";

    public override string Command => "chatgpt";
    public override string[] Aliases => new[] { "gpt", JaredCommand };

    private readonly Logger _logger;
    private readonly IConfigurationService _configurationService;
    private readonly string[] _commandAndAliases;
    private readonly Dictionary<ulong, ChatHistory> _chatHistory = new(), _jaredChatHistory = new();
    private readonly OpenAIService _openAI;

    public ChatGptComand(Logger logger, IConfigurationService configurationService, OpenAIService openAI)
    {
        _logger = logger;
        _configurationService = configurationService;
        _openAI = openAI;
        _commandAndAliases = Enumerable.Concat(Aliases, new string[] { Command }).ToArray();
    }

    private sealed class ChatHistory
    {
        private sealed record HistoryEntry(ChatMessage Message, DateTime Timestamp);

        private readonly List<HistoryEntry> _entries = new();

        public SemaphoreSlim Lock { get; set; } = new SemaphoreSlim(1);

        public void AddUserPrompt(SocketGuildUser user, string prompt)
        {
            _entries.Add(new HistoryEntry(ChatMessage.CreateUserMessage($"{user.GlobalName ?? user.Username}: {prompt}"), DateTime.UtcNow));
        }

        public void AddAssistantResponse(string response)
        {
            _entries.Add(new HistoryEntry(ChatMessage.CreateAssistantMessage(response), DateTime.UtcNow));
        }

        public List<ChatMessage> GetChatMessages(string systemPrompt, int maxChatHistory)
        {
            _entries.RemoveAll(e => (DateTime.UtcNow - e.Timestamp) > TimeSpan.FromHours(2));

            if (_entries.Count > maxChatHistory)
            {
                _entries.RemoveRange(0, _entries.Count - maxChatHistory);
            }

            List<ChatMessage> messages = _entries.Select(e => e.Message).ToList();

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                messages.Insert(0, ChatMessage.CreateSystemMessage(systemPrompt));
            }

            return messages;
        }
    }

    public override Task ExecuteAsync(CommandContext ctx)
    {
        return HandleAsync(ctx.Channel, ctx.Author, ctx.Command, ctx.ArgumentStringTrimmed);
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        string content = ctx.Content;

        foreach (string command in _commandAndAliases)
        {
            if (content.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                return HandleAsync(ctx.Channel, ctx.Author, command, content.Substring(command.Length).Trim());
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SocketTextChannel channel, SocketGuildUser author, string command, string prompt)
    {
        if (!ProgramState.AzureEnabled)
        {
            return;
        }

        bool isJared = command.Equals(JaredCommand, StringComparison.OrdinalIgnoreCase);

        if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.MaxTokens", out string maxTokensString) ||
            !int.TryParse(maxTokensString, out int maxTokens) ||
            maxTokens is < 0 or > 2048)
        {
            maxTokens = 400;
        }

        if (!_configurationService.TryGet(channel.Guild.Id, "ChatGPT.MaxChatHistory", out string maxChatHistoryString) ||
            !int.TryParse(maxChatHistoryString, out int maxChatHistory) ||
            maxChatHistory is < 0 or > 1000)
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

        ChatClient client = _openAI.GetChat(channel.Guild.Id);

        var options = new ChatCompletionOptions
        {
            EndUserId = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"Discord_{channel.Id}_{author.Id}"))),
            MaxOutputTokenCount = maxTokens
        };

        var chatHistoryCollection = isJared ? _jaredChatHistory : _chatHistory;
        ChatHistory chatHistory;

        lock (chatHistoryCollection)
        {
            chatHistory = CollectionsMarshal.GetValueRefOrAddDefault(chatHistoryCollection, channel.Id, out _) ??= new();
        }

        await chatHistory.Lock.WaitAsync();
        try
        {
            chatHistory.AddUserPrompt(author, prompt);

            Stopwatch stopwatch = Stopwatch.StartNew();
            Task<RestUserMessage> sendMessageTask = null;
            List<StreamingChatCompletionUpdate> updates = new();
            string lastUpdateText = string.Empty;

            async Task UpdateMessageAsync(bool final = false)
            {
                string currentText = string.Concat(updates.SelectMany(u => u.ContentUpdate).SelectMany(u => u.Text));
                currentText = currentText.TruncateWithDotDotDot(2000);

                if (lastUpdateText == currentText)
                {
                    return;
                }

                bool shouldSkip =
                    (currentText.Length - lastUpdateText.Length) < 50 ||
                    stopwatch.Elapsed.TotalSeconds < 0.5;

                if (shouldSkip && !final)
                {
                    return;
                }

                _logger.DebugLog($"ChatGPT response for {author.Id} in channel={channel.Id} with model={updates[0].Model} maxTokens={maxTokens} prompt='{prompt}' was '{currentText}'");

                if (sendMessageTask is null)
                {
                    sendMessageTask = channel.SendMessageAsync(currentText);
                }
                else
                {
                    var message = await sendMessageTask;
                    sendMessageTask = Task.Run(async () =>
                    {
                        await message.ModifyAsync(m => m.Content = currentText);
                        return message;
                    });
                }

                lastUpdateText = currentText;
                stopwatch.Restart();
            }

            List<ChatMessage> messages = chatHistory.GetChatMessages(systemPrompt, maxChatHistory);

            await foreach (StreamingChatCompletionUpdate completionUpdate in client.CompleteChatStreamingAsync(messages, options))
            {
                updates.Add(completionUpdate);
                await UpdateMessageAsync();
            }

            await UpdateMessageAsync(final: true);
            await sendMessageTask;

            chatHistory.AddAssistantResponse(lastUpdateText);
        }
        finally
        {
            chatHistory.Lock.Release();
        }
    }
}
