using Azure.AI.OpenAI;
using Azure;
using OpenAI.Images;
using MihuBot.Configuration;
using Microsoft.Extensions.AI;

#nullable enable

namespace MihuBot.Helpers;

public sealed record ModelInfo(string Name, int ContextSize);

public sealed class OpenAIService
{
    public const string DefaultModel = "gpt-5-mini";

    public static readonly ModelInfo[] AllModels =
    [
        new("gpt-5", 400_000),
        new("gpt-5-mini", 400_000),
        new("gpt-5-nano", 400_000),
    ];

    private readonly Logger _logger;
    private readonly AzureOpenAIClient _chat;
    private readonly AzureOpenAIClient _image;
    private readonly AzureOpenAIClient _secondaryEmbeddingClient;
    private readonly AzureOpenAIClient _secondaryChatClient;
    private readonly IConfigurationService _configurationService;

    public OpenAIService(IConfiguration configuration, IConfigurationService configurationService, Logger logger)
    {
        _configurationService = configurationService;
        _logger = logger;

        _chat = new AzureOpenAIClient(
            new Uri("https://mihubotai8467177614.openai.azure.com"),
            new AzureKeyCredential(configuration["AzureOpenAI:Key"] ?? throw new InvalidOperationException("Missing AzureOpenAI Key")));

        _image = new AzureOpenAIClient(
            new Uri("https://mihaz-m30zd4gd-eastus.openai.azure.com"),
            new AzureKeyCredential(configuration["AzureOpenAI:ImageKey"] ?? throw new InvalidOperationException("Missing AzureOpenAI Image Key")));

        _secondaryEmbeddingClient = new AzureOpenAIClient(
            new Uri(configuration["AzureOpenAI:SecondaryEmbedding:Endpoint"] ?? throw new InvalidOperationException("Missing secondary embedding endpoint")),
            new AzureKeyCredential(configuration["AzureOpenAI:SecondaryEmbedding:Key"] ?? throw new InvalidOperationException("Missing AzureOpenAI secondary embedding Key")));

        _secondaryChatClient = new AzureOpenAIClient(
            new Uri(configuration["AzureOpenAI:SecondaryChat:Endpoint"] ?? throw new InvalidOperationException("Missing secondary chat endpoint")),
            new AzureKeyCredential(configuration["AzureOpenAI:SecondaryChat:Key"] ?? throw new InvalidOperationException("Missing AzureOpenAI secondary chat Key")));
    }

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string deployment, bool secondary = false)
    {
        AzureOpenAIClient client = secondary ? _secondaryEmbeddingClient : _chat;
        return client.GetEmbeddingClient(deployment).AsIEmbeddingGenerator();
    }

    public IChatClient GetChat(ulong? context)
    {
        _configurationService.TryGet(context, "ChatGPT.Deployment", out string? deployment);
        bool secondary = _configurationService.GetOrDefault(context, "ChatGPT.Secondary", false);

        return GetChat(deployment, secondary);
    }

    public IChatClient GetChat(string deployment, bool secondary = false)
    {
        deployment ??= DefaultModel;

        AzureOpenAIClient client = secondary ? _secondaryChatClient : _chat;
        IChatClient chatClient = client.GetChatClient(deployment).AsIChatClient();

        chatClient = new LoggingChatClient(chatClient, _logger, _configurationService);

        return chatClient;
    }

    public ImageClient GetImage(ulong? context)
    {
        _configurationService.TryGet(context, "ChatGPT.ImageDeployment", out string? deployment);

        deployment ??= "dall-e-3";

        return _image.GetImageClient(deployment);
    }

    public async Task<string> GetSimpleChatCompletionAsync(ulong? context, string prompt)
    {
        IChatClient chatClient = GetChat(context);

        ChatResponse chatResponse = await chatClient.GetResponseAsync(prompt);

        string response = chatResponse.Text;

        _logger.DebugLog($"ChatGPT response for '{prompt}' for {context}: '{response}'");

        return response;
    }
}
