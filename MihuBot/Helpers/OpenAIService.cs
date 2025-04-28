using Azure.AI.OpenAI;
using Azure;
using OpenAI.Images;
using MihuBot.Configuration;
using Microsoft.Extensions.AI;

#nullable enable

namespace MihuBot.Helpers;

public sealed class OpenAIService
{
    private readonly Logger _logger;
    private readonly AzureOpenAIClient _chat;
    private readonly AzureOpenAIClient _image;
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
    }

    public IChatClient GetChat(ulong? context)
    {
        _configurationService.TryGet(context, "ChatGPT.Deployment", out string? deployment);

        deployment ??= "gpt-4o";

        return _chat.GetChatClient(deployment).AsIChatClient();
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
