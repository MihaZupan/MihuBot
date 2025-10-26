using MihuBot.Configuration;
using OpenAI.Images;

namespace MihuBot.Commands;

public sealed class ImagineCommand : CommandBase
{
    public override string Command => "imagine";

    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(5);
    protected override int CooldownToleranceCount => 10;

    private readonly Logger _logger;
    private readonly IConfigurationService _configurationService;
    private readonly OpenAIService _openAI;

    public ImagineCommand(Logger logger, IConfigurationService configurationService, OpenAIService openAI)
    {
        _logger = logger;
        _configurationService = configurationService;
        _openAI = openAI;
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        if (ctx.Content.Equals("imagine", StringComparison.OrdinalIgnoreCase) &&
            GetContentFromMessageReference(ctx) is { Content: not null } prompt &&
            TryEnter(ctx))
        {
            return ExecuteAsync(ctx, prompt.Content);
        }

        return Task.CompletedTask;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        await ExecuteAsync(ctx, ctx.ArgumentStringTrimmed);
    }

    public static (string Content, SocketGuildUser Author) GetContentFromMessageReference(MessageContext ctx)
    {
        if (ctx.Message.ReferencedMessage is { } referencedMessage &&
            referencedMessage.Content is not null &&
            referencedMessage.CleanContent is { } cleanContent &&
            !string.IsNullOrWhiteSpace(cleanContent) &&
            referencedMessage.Attachments is null or { Count: 0 })
        {
            return (cleanContent, ctx.Author);
        }

        return (null, null);
    }

    private async Task ExecuteAsync(MessageContext ctx, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = GetContentFromMessageReference(ctx).Content;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (!_configurationService.TryGet(ctx.Guild.Id, "ChatGPT.ImagePromptPrompt", out string promptPrompt))
            {
                promptPrompt = "What's a cool prompt for Dall-e 3? Please reply with only the prompt, without quotes.";
            }

            prompt = await _openAI.GetSimpleChatCompletionAsync(ctx.Guild.Id, promptPrompt);
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

        _logger.DebugLog($"{nameof(ImagineCommand)} prompt: {prompt}");

        ImageClient client = _openAI.GetImage(ctx.Guild.Id);

        using var typing = ctx.Channel.EnterTypingState();

        GeneratedImage image;
        try
        {
            image = (await client.GenerateImageAsync(prompt, new ImageGenerationOptions
            {
                EndUserId = $"Discord_{ctx.Channel.Id}_{ctx.AuthorId}".GetUtf8Sha3_512HashBase64Url(),
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
