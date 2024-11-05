using Azure;
using Azure.AI.OpenAI;
using MihuBot.Configuration;
using OpenAI.Chat;
using OpenAI.Images;
using System.Security.Cryptography;

namespace MihuBot.Commands;

public sealed class ImagineCommand : CommandBase
{
    public override string Command => "imagine";

    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(5);
    protected override int CooldownToleranceCount => 10;

    private readonly Logger _logger;
    private readonly IConfigurationService _configurationService;
    private readonly AzureOpenAIClient _openAI;
    private readonly AzureOpenAIClient _openAIChat;

    public ImagineCommand(Logger logger, IConfiguration configuration, IConfigurationService configurationService, IEnumerable<AzureOpenAIClient> openAI)
    {
        _logger = logger;
        _configurationService = configurationService;

        _openAI = new AzureOpenAIClient(
            new Uri("https://mihaz-m30zd4gd-eastus.openai.azure.com"),
            new AzureKeyCredential(configuration["AzureOpenAI:ImageKey"]));

        _openAIChat = new AzureOpenAIClient(
            new Uri("https://mihubotai8467177614.openai.azure.com"),
            new AzureKeyCredential(configuration["AzureOpenAI:Key"]));
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        string prompt = ctx.ArgumentStringTrimmed;

        if (string.IsNullOrWhiteSpace(prompt))
        {
            var promptPrompt = ChatMessage.CreateUserMessage("What's a cool prompt for Dall-e 3? Please reply with only the prompt, without quotes.");
            ChatCompletion completion = (await _openAIChat.GetChatClient("gpt-4o").CompleteChatAsync(promptPrompt)).Value;
            prompt = string.Concat(completion.Content.SelectMany(u => u.Text));
        }

        GeneratedImageSize size = GeneratedImageSize.W1024xH1024;

        if (prompt.StartsWith("large ", StringComparison.OrdinalIgnoreCase))
        {
            prompt = prompt.Substring("large ".Length);
            size = GeneratedImageSize.W1792xH1024;
        }
        else if (prompt.StartsWith("tall ", StringComparison.OrdinalIgnoreCase))
        {
            prompt = prompt.Substring("tall ".Length);
            size = GeneratedImageSize.W1024xH1792;
        }

        if (!_configurationService.TryGet(ctx.Guild.Id, "ChatGPT.ImageDeployment", out string deployment))
        {
            deployment = "dall-e-3";
        }

        ImageClient client = _openAI.GetImageClient(deployment);

        using var typing = ctx.Channel.EnterTypingState();

        GeneratedImage image;
        try
        {
            image = (await client.GenerateImageAsync(prompt, new ImageGenerationOptions
            {
                EndUserId = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"Discord_{ctx.Channel.Id}_{ctx.AuthorId}"))),
                ResponseFormat = GeneratedImageFormat.Bytes,
                Quality = GeneratedImageQuality.High,
                Size = size,
            })).Value;
        }
        catch (Exception ex)
        {
            _logger.DebugLog($"Image generation failed for prompt '{prompt}': {ex}");
            await ctx.Message.AddReactionAsync(Emotes.RedCross);
            return;
        }

        await ctx.Channel.SendFileAsync(image.ImageBytes.ToStream(), $"{ctx.Message.Id}.png");
    }
}
