using Azure.AI.OpenAI;
using Azure;
using OpenAI.Images;
using MihuBot.Configuration;
using Microsoft.Extensions.AI;

#nullable enable

namespace MihuBot.Helpers.AI;

public sealed record ModelInfo(
    string Name,
    int ContextSize,
    decimal InputUsdPerMillionTokens,
    decimal CachedInputUsdPerMillionTokens,
    decimal OutputUsdPerMillionTokens,
    int? LongContextThreshold = null);

public sealed class OpenAIService
{
    public const string DefaultModel = "gpt-5.6-luna";

    // Standard USD rates, https://developers.openai.com/api/docs/pricing (2026-09-16).
    // Estimates, not Azure region/deployment-specific billing rates. Above LongContextThreshold,
    // input/cache rates double and output rates increase by 50% for the full request.
    public static readonly ModelInfo[] AllModels =
    [
        new("gpt-5.6-luna", 1_050_000, 0.20m, 0.02m, 1.20m, LongContextThreshold: 272_000),
        new("gpt-5.6-terra", 1_050_000, 2m, 0.20m, 12m, LongContextThreshold: 272_000),
        new("gpt-5.6-sol", 1_050_000, 4m, 0.40m, 20m, LongContextThreshold: 272_000),
        new("gpt-6-astra", 1_050_000, 10m, 1m, 50m, LongContextThreshold: 272_000),
        new("gpt-5", 400_000, 1.25m, 0.125m, 10m),
        new("gpt-5-mini", 400_000, 0.25m, 0.025m, 2m),
        new("gpt-5-nano", 400_000, 0.05m, 0.005m, 0.40m),
    ];

    private readonly Logger _logger;
    private readonly AzureOpenAIClient _chat;
    private readonly AzureOpenAIClient? _image;
    private readonly AzureOpenAIClient? _secondaryEmbeddingClient;
    private readonly AzureOpenAIClient? _secondaryChatClient;
    private readonly IConfigurationService _configurationService;

    /// <summary>False when no image generation endpoint is configured.</summary>
    public bool ImageEnabled => _image is not null;

    public OpenAIService(IConfiguration configuration, IConfigurationService configurationService, Logger logger)
    {
        _configurationService = configurationService;
        _logger = logger;

        // Only the primary endpoint is required, the rest fall back to it when not configured.
        _chat = new AzureOpenAIClient(
            new Uri("https://mihubotai8467177614.openai.azure.com"),
            new AzureKeyCredential(configuration["AzureOpenAI:Key"] ?? throw new InvalidOperationException("Missing AzureOpenAI Key")));

        if (configuration.IsConfigured(OptionalFeatures.AzureOpenAIImage))
        {
            _image = new AzureOpenAIClient(
                new Uri("https://mihaz-m30zd4gd-eastus.openai.azure.com"),
                new AzureKeyCredential(configuration["AzureOpenAI:ImageKey"]!));
        }

        if (configuration.IsConfigured(OptionalFeatures.AzureOpenAISecondaryEmbedding))
        {
            _secondaryEmbeddingClient = new AzureOpenAIClient(
                new Uri(configuration["AzureOpenAI:SecondaryEmbedding:Endpoint"]!),
                new AzureKeyCredential(configuration["AzureOpenAI:SecondaryEmbedding:Key"]!));
        }

        if (configuration.IsConfigured(OptionalFeatures.AzureOpenAISecondaryChat))
        {
            _secondaryChatClient = new AzureOpenAIClient(
                new Uri(configuration["AzureOpenAI:SecondaryChat:Endpoint"]!),
                new AzureKeyCredential(configuration["AzureOpenAI:SecondaryChat:Key"]!));
        }
    }

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string deployment, bool secondary = false)
    {
        AzureOpenAIClient client = (secondary ? _secondaryEmbeddingClient : null) ?? _chat;
        return client.GetEmbeddingClient(deployment).AsIEmbeddingGenerator();
    }

    public IChatClient GetChat(ulong? context)
    {
        _configurationService.TryGet(context, "ChatGPT.Deployment", out string? deployment);
        bool secondary = _configurationService.GetOrDefault(context, "ChatGPT.Secondary", false);
        bool tertiary = _configurationService.GetOrDefault(context, "ChatGPT.Tertiary", false);

        return GetChat(deployment, tertiary ? 3 : secondary ? 2 : 1);
    }

    public IChatClient GetChat(string deployment, bool secondary) =>
        GetChat(deployment, secondary ? 2 : 1);

    public IChatClient GetChat(string deployment, int deploymentOption = 1)
    {
        deployment ??= DefaultModel;

        AzureOpenAIClient client = (deploymentOption == 3 ? _image : null) ?? (deploymentOption == 2 ? _secondaryChatClient : null) ?? _chat;
        IChatClient chatClient = client.GetChatClient(deployment).AsIChatClient();

        chatClient = new LoggingChatClient(chatClient, _logger, _configurationService);

        return chatClient;
    }

    public ImageClient? GetImage(ulong? context)
    {
        if (_image is null)
        {
            return null;
        }

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
