using System.ClientModel;
using OpenAI;
using OpenAI.Images;
using MihuBot.Configuration;
using Microsoft.Extensions.AI;
using MihuBot.RuntimeUtils;

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
    public const string DefaultModel = "gpt-6-luna";

    // Standard USD rates, https://developers.openai.com/api/docs/pricing (2026-09-22).
    // Estimates, not Azure region/deployment-specific billing rates. Above LongContextThreshold,
    // input/cache rates double and output rates increase by 50% for the full request.
    public static readonly ModelInfo[] AllModels =
    [
        new("gpt-5.6-luna", 1_050_000, 0.20m, 0.02m, 1.20m, LongContextThreshold: 272_000),
        new("gpt-5.6-terra", 1_050_000, 2m, 0.20m, 12m, LongContextThreshold: 272_000),
        new("gpt-5.6-sol", 1_050_000, 4m, 0.40m, 20m, LongContextThreshold: 272_000),
        new("gpt-6-luna", 1_050_000, 0.10m, 0.01m, 0.50m, LongContextThreshold: 272_000),
        new("gpt-6-sol", 1_050_000, 2m, 0.20m, 10m, LongContextThreshold: 272_000),
        new("gpt-6-astra", 1_050_000, 10m, 1m, 50m, LongContextThreshold: 272_000),
    ];

    private readonly Logger _logger;

    private readonly List<(OpenAIClient Client, bool Work, string[] Deployments)> _clients = [];
    private readonly IConfigurationService _configurationService;

    public OpenAIService(IConfiguration configuration, IConfigurationService configurationService, Logger logger)
    {
        _configurationService = configurationService;
        _logger = logger;

        if (!configuration.IsConfigured(OptionalFeatures.AzureOpenAI))
        {
            throw new InvalidOperationException("Missing AzureOpenAI:Key");
        }

        string[] gpt56Family = ["gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol"];
        string[] gpt6LunaSol = ["gpt-6-luna", "gpt-6-sol"];
        const string Gpt6Astra = "gpt-6-astra";
        string[] embeddings = ["text-embedding-3-small", "text-embedding-3-large"];

        AddClient(OptionalFeatures.AzureOpenAI, "mihubotai8467177614", work: false, [.. gpt56Family, Gpt6Astra, .. embeddings]);
        AddClient(OptionalFeatures.AzureOpenAI2, "mihaz-m30zd4gd-eastus", work: false, [.. gpt56Family, Gpt6Astra]);
        AddClient(OptionalFeatures.AzureOpenAI3, "mizup-mud33obs-swedencentral", work: false, gpt6LunaSol);
        AddClient(OptionalFeatures.AzureOpenAIWork1, "issueshelperhu5783781236", work: true, [.. gpt6LunaSol, .. embeddings]);
        AddClient(OptionalFeatures.AzureOpenAIWork2, "mizup-ma441ssi-eastus2", work: true, [.. gpt56Family, Gpt6Astra]);

        void AddClient(OptionalFeature feature, string resourceName, bool work, string[] deployments)
        {
            if (configuration.IsConfigured(feature))
            {
                // The v1 API exposes newer reasoning levels without a dated Azure API version.
                var client = new OpenAIClient(
                    new ApiKeyCredential(configuration[feature.Keys[0]]!),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri($"https://{resourceName}.openai.azure.com/openai/v1/"),
                    });

                _clients.Add((client, work, deployments));
            }
        }
    }

    private OpenAIClient GetClient(string deployment, bool work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);

        List<OpenAIClient> matchingWorkClients = [];
        List<OpenAIClient> matchingPersonalClients = [];

        foreach ((OpenAIClient client, bool isWork, string[] deployments) in _clients)
        {
            if (!deployments.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (isWork && work)
            {
                matchingWorkClients.Add(client);
            }
            else if (!isWork)
            {
                matchingPersonalClients.Add(client);
            }
        }

        if (matchingWorkClients.Count > 0)
        {
            return matchingWorkClients.Random();
        }

        if (matchingPersonalClients.Count > 0)
        {
            return matchingPersonalClients.Random();
        }

        throw new InvalidOperationException(
            $"No configured {(work ? "work or personal" : "personal")} Azure OpenAI endpoint hosts deployment '{deployment}'.");
    }

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string deployment, bool work)
    {
        OpenAIClient client = GetClient(deployment, work);
        return client.GetEmbeddingClient(deployment).AsIEmbeddingGenerator()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: MihuBotAIActivitySource.Instance.Name)
            .Build();
    }

    public IChatClient GetChat(ulong? context)
    {
        _configurationService.TryGet(context, "ChatGPT.Deployment", out string? deployment);
        bool work = _configurationService.GetOrDefault(context, "ChatGPT.Work", false);

        return GetChat(deployment, work);
    }

    public IChatClient GetChat(string? deployment, bool work)
    {
        deployment ??= DefaultModel;

        OpenAIClient client = GetClient(deployment, work);
        IChatClient chatClient = client.GetChatClient(deployment).AsIChatClient();

        chatClient = new LoggingChatClient(chatClient, _logger, _configurationService);

        return chatClient.AsBuilder()
            .UseOpenTelemetry(sourceName: MihuBotAIActivitySource.Instance.Name)
            .Build();
    }

    public IChatClient GetResponsesChat(string? deployment, bool work)
    {
        deployment ??= DefaultModel;

        OpenAIClient client = GetClient(deployment, work);
        IChatClient chatClient = client.GetResponsesClient().AsIChatClient(deployment);

        return new LoggingChatClient(chatClient, _logger, _configurationService)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: MihuBotAIActivitySource.Instance.Name)
            .Build();
    }

    public ImageClient? GetImage(ulong? context)
    {
        _configurationService.TryGet(context, "ChatGPT.ImageDeployment", out string? deployment);

        if (string.IsNullOrWhiteSpace(deployment))
        {
            return null;
        }

        foreach ((OpenAIClient client, bool isWork, string[] deployments) in _clients)
        {
            if (!isWork && deployments.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            {
                return client.GetImageClient(deployment);
            }
        }

        return null;
    }

    public async Task<string> GetSimpleChatCompletionAsync(ulong? context, string prompt)
    {
        using IChatClient chatClient = GetChat(context);

        ChatResponse chatResponse = await chatClient.GetResponseAsync(prompt);

        string response = chatResponse.Text;

        _logger.DebugLog($"ChatGPT response for '{prompt}' for {context}: '{response}'");

        return response;
    }
}
