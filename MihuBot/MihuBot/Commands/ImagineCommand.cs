using Azure;
using Azure.AI.OpenAI;
using MihuBot.Configuration;
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

    public ImagineCommand(Logger logger, IConfiguration configuration, IConfigurationService configurationService, IEnumerable<AzureOpenAIClient> openAI)
    {
        _logger = logger;
        _configurationService = configurationService;
        _openAI = new AzureOpenAIClient(
            new Uri("https://mihaz-m30zd4gd-eastus.openai.azure.com"),
            new AzureKeyCredential(configuration["AzureOpenAI:ImageKey"]));
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        string prompt = ctx.ArgumentStringTrimmed;

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = ctx.Author.GlobalName ?? ctx.Author.Username;
        }

        GeneratedImageSize size = GeneratedImageSize.W1024xH1024;

        if (prompt.StartsWith("large ", StringComparison.OrdinalIgnoreCase))
        {
            prompt = prompt.Substring("large ".Length);
            size = GeneratedImageSize.W1792xH1024;
        }

        if (!_configurationService.TryGet(ctx.Guild.Id, "ChatGPT.ImageDeployment", out string deployment))
        {
            deployment = "dall-e-3";
        }

        ImageClient client = _openAI.GetImageClient(deployment);

        using var typing = ctx.Channel.EnterTypingState();

        GeneratedImage image = (await client.GenerateImageAsync(prompt, new ImageGenerationOptions
        {
            EndUserId = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"Discord_{ctx.Channel.Id}_{ctx.AuthorId}"))),
            ResponseFormat = GeneratedImageFormat.Bytes,
            Quality = GeneratedImageQuality.High,
            Size = size,
        })).Value;

        await ctx.Channel.SendFileAsync(image.ImageBytes.ToStream(), $"{ctx.Message.Id}.png");
    }
}
