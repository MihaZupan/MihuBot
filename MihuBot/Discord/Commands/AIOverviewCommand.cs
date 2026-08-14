using Microsoft.Extensions.AI;
using MihuBot.Commands;
using MihuBot.Configuration;
using MihuBot.Helpers.AI;
using System.Text;

namespace MihuBot.Discord.Commands;

public sealed class AIOverviewCommand : CommandBase
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(1);

    private const int MaxMessagesToFetch = 2000;
    private const int MaxTranscriptLength = 100_000;

    public override string Command => "aioverview";
    public override string[] Aliases => ["overview", "tldr", "summarize"];

    protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);
    protected override int CooldownToleranceCount => 3;

    private readonly Logger _logger;
    private readonly IConfigurationService _configurationService;
    private readonly OpenAIService _openAI;

    public AIOverviewCommand(Logger logger, IConfigurationService configurationService, OpenAIService openAI)
    {
        _logger = logger;
        _configurationService = configurationService;
        _openAI = openAI;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ProgramState.AzureEnabled)
        {
            return;
        }

        string argument = ctx.ArgumentStringTrimmed;

        TimeSpan duration = DefaultDuration;

        if (!string.IsNullOrEmpty(argument))
        {
            if (!TryParseDuration(argument, out duration))
            {
                await ctx.ReplyAsync("Please specify a valid duration like `!aioverview 2 hours` (defaults to 1h, max 1 day)", mention: true);
                return;
            }

            if (duration > MaxDuration)
            {
                duration = MaxDuration;
            }
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - duration;

        (string transcript, int messageCount) = await GetTranscriptAsync(ctx, cutoff, ctx.CancellationToken);

        if (messageCount == 0)
        {
            await ctx.ReplyAsync($"There were no messages in the last {duration.ToElapsedTime(includeSeconds: false)}", mention: true);
            return;
        }

        IChatClient client = _openAI.GetChat(ctx.Guild.Id);

        if (!_configurationService.TryGet(ctx.Guild.Id, "AIOverview.SystemPrompt", out string systemPrompt))
        {
            systemPrompt =
                """
                You are summarizing a Discord channel conversation for someone who hasn't been reading it.
                Write a short overview of what was discussed, grouped by topic, mentioning who was involved and any conclusions or action items.
                Use a few bullet points, keep it under 250 words, and don't invent anything that isn't in the transcript.
                """;
        }

        var options = new ChatOptions
        {
            MaxOutputTokens = 800,
            RawRepresentationFactory = _ => new OpenAI.Chat.ChatCompletionOptions
            {
                ReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel.Medium,
            },
        };

        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User,
                $"""
                Here is the transcript of the last {duration.ToElapsedTime(includeSeconds: false)} in #{ctx.Channel.Name} ({messageCount} messages, oldest first):

                {transcript}
                """)
        ];

        string response;
        try
        {
            ChatResponse chatResponse = await client.GetResponseAsync(messages, options, ctx.CancellationToken);
            response = chatResponse.Text;
        }
        catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.DebugLog(ex, $"Failed to generate an overview for {ctx.Channel.Id}");
            await ctx.ReplyAsync("Sorry, something went wrong while generating the overview", mention: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            await ctx.ReplyAsync("Sorry, I couldn't come up with an overview", mention: true);
            return;
        }

        _logger.DebugLog($"AI overview for {ctx.AuthorId} in channel={ctx.Channel.Id} over {duration} ({messageCount} messages) was '{response}'");

        var embed = new EmbedBuilder()
            .WithTitle($"Overview of the last {duration.ToElapsedTime(includeSeconds: false)}")
            .WithDescription(response.TruncateWithDotDotDot(4000))
            .WithFooter($"Based on {messageCount} message{(messageCount == 1 ? "" : "s")}")
            .WithColor(new Color(88, 101, 242));

        await ctx.Channel.SendMessageAsync(embed: embed.Build());
    }

    private static async Task<(string Transcript, int MessageCount)> GetTranscriptAsync(CommandContext ctx, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        List<IMessage> collected = [];

        await foreach (IReadOnlyCollection<IMessage> page in ctx.Channel.GetMessagesAsync(MaxMessagesToFetch, options: new RequestOptions { CancelToken = cancellationToken }).WithCancellation(cancellationToken))
        {
            bool reachedCutoff = false;

            foreach (IMessage message in page)
            {
                if (message.Timestamp < cutoff)
                {
                    reachedCutoff = true;
                    continue;
                }

                if (ctx.StartedAt - message.Timestamp < TimeSpan.FromSeconds(1))
                {
                    continue;
                }

                collected.Add(message);
            }

            if (reachedCutoff)
            {
                break;
            }
        }

        var builder = new StringBuilder();
        int messageCount = 0;

        foreach (IMessage message in collected.OrderBy(m => m.Timestamp))
        {
            string content = message.Content?.Trim();

            if (message.Attachments.Count > 0)
            {
                string attachments = string.Join(", ", message.Attachments.Select(a => a.Filename));
                content = string.IsNullOrEmpty(content) ? $"[attachments: {attachments}]" : $"{content} [attachments: {attachments}]";
            }

            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            messageCount++;

            builder.Append('[').Append(message.Timestamp.UtcDateTime.ToString("HH:mm")).Append("] ");
            builder.Append(message.Author.GetName()).Append(": ");
            builder.AppendLine(content.NormalizeNewLines().Replace('\n', ' '));

            if (builder.Length > MaxTranscriptLength)
            {
                break;
            }
        }

        return (builder.ToString(), messageCount);
    }

    /// <summary>Parses durations like "2 hours" or "30 min" using the same logic as the reminder command.</summary>
    public static bool TryParseDuration(string argument, out TimeSpan duration)
    {
        duration = default;

        DateTime now = DateTime.UtcNow;

        if (!ReminderCommand.TryParseRemindTimeCore(argument, now, out DateTime time))
        {
            return false;
        }

        duration = time - now;
        return duration > TimeSpan.Zero;
    }
}
