using Microsoft.Extensions.AI;
using MihuBot.Commands;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.Helpers.AI;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MihuBot.Discord.Commands;

public sealed class AIOverviewCommand : CommandBase
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(365);

    private const int MaxTranscriptLength = 500_000;
    private const string FocusMarker = ">>";

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

        string argument = ctx.ArgumentLines.FirstOrDefault() ?? string.Empty;
        string extraContext = string.Join('\n', ctx.ArgumentLines.Skip(1)).Trim();

        IUser mentionedUser = ctx.Message.MentionedUsers.FirstOrDefault(u => u.Id != ctx.BotId);

        (string durationArgument, string userArgument) = SplitArguments(argument, ctx.Message.MentionedUsers.Select(u => u.Id));

        TimeSpan duration = DefaultDuration;

        if (!string.IsNullOrEmpty(durationArgument))
        {
            if (!TryParseDuration(durationArgument, out duration))
            {
                await ctx.ReplyAsync("Please specify a valid duration like `!aioverview 2 hours` (defaults to 1 day, max 1 year). You can also filter by person: `!aioverview 2 hours from @someone`", mention: true);
                return;
            }

            if (duration > MaxDuration && !ctx.IsFromAdmin)
            {
                duration = MaxDuration;
            }
        }

        Task.Run(() => ctx.Message.AddReactionAsync(Emotes.Stopwatch)).IgnoreExceptions();

        IUser filterUser = mentionedUser;

        if (filterUser is null && !string.IsNullOrEmpty(userArgument))
        {
            filterUser = await ResolveUserAsync(ctx, userArgument);

            if (filterUser is null)
            {
                await ctx.ReplyAsync($"I don't know who `{userArgument.TruncateWithDotDotDot(64)}` is", mention: true);
                return;
            }
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - duration;

        (string transcript, int messageCount, int focusMessageCount) = await GetTranscriptAsync(ctx, cutoff, filterUser?.Id, ctx.CancellationToken);

        if (messageCount == 0)
        {
            await ctx.ReplyAsync($"There were no messages in the last {duration.ToElapsedTime(includeSeconds: false)}", mention: true);
            return;
        }

        if (filterUser is not null && focusMessageCount == 0)
        {
            await ctx.ReplyAsync($"There were no messages from {filterUser.GetName()} in the last {duration.ToElapsedTime(includeSeconds: false)}", mention: true);
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

            if (filterUser is not null)
            {
                systemPrompt =
                    $"""
                    {systemPrompt}
                    The reader only cares about {filterUser.GetName()}, so focus the summary on what they said, asked, and were involved in.
                    Their messages are marked with a {FocusMarker} prefix in the transcript.
                    The rest of the conversation is included only as context - mention it just when it's needed to make sense of {filterUser.GetName()}'s messages, such as questions they answered or replies they got.
                    """;
            }
        }

        if (!string.IsNullOrEmpty(extraContext))
        {
            systemPrompt =
                $"""
                {systemPrompt}

                {extraContext}
                """;
        }

        var options = new ChatOptions
        {
            MaxOutputTokens = 50_000,
            RawRepresentationFactory = _ => new OpenAI.Chat.ChatCompletionOptions
            {
                ReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel.High,
            },
        };

        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User,
                $"""
                Here is the transcript of the last {duration.ToElapsedTime(includeSeconds: false)} in #{ctx.Channel.Name} ({messageCount} messages, oldest first{(filterUser is null ? "" : $", {focusMessageCount} of them from {filterUser.GetName()}")}):

                {transcript}
                """)
        ];

        string response;
        UsageDetails usage;
        try
        {
            ChatResponse chatResponse = await client.GetResponseAsync(messages, options, ctx.CancellationToken);
            response = chatResponse.Text;
            usage = chatResponse.Usage;
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

        _logger.DebugLog($"AI overview for {ctx.AuthorId} in channel={ctx.Channel.Id} over {duration} (focusUser={filterUser?.Id.ToString() ?? "none"}, {messageCount} messages, {focusMessageCount} focused) was '{response}'");

        string footer = $"Based on {messageCount} message{(messageCount == 1 ? "" : "s")}{(filterUser is null ? "" : $", {focusMessageCount} from {filterUser.GetName()}")}";

        if (usage is { InputTokenCount: not null } or { OutputTokenCount: not null })
        {
            footer = $"{footer} • {FormatTokenCount(usage.InputTokenCount ?? 0)} tokens in, {FormatTokenCount(usage.OutputTokenCount ?? 0)} out";
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Overview of the last {duration.ToElapsedTime(includeSeconds: false)}{(filterUser is null ? "" : $" focused on {filterUser.GetName()}")}".TruncateWithDotDotDot(256))
            .WithDescription(response.TruncateWithDotDotDot(4000))
            .WithFooter(footer.TruncateWithDotDotDot(2048))
            .WithColor(new Color(88, 101, 242));

        await ctx.Channel.SendMessageAsync(embed: embed.Build());
    }

    private async Task<(string Transcript, int MessageCount, int FocusMessageCount)> GetTranscriptAsync(CommandContext ctx, DateTimeOffset cutoff, ulong? focusUserId, CancellationToken cancellationToken)
    {
        long channelId = (long)ctx.Channel.Id;

        Task<ulong[]> botsTask = ctx.Guild.GetUsersAsync().Flatten().Where(u => u.IsBot).Select(u => u.Id).ToArrayAsync(cancellationToken).AsTask();

        LogDbEntry[] entries = await _logger.GetLogsAsync(
            cutoff.UtcDateTime,
            DateTime.UtcNow - TimeSpan.FromSeconds(2),
            query => query.Where(log =>
                log.ChannelId == channelId &&
                (log.Type == Logger.EventType.MessageReceived ||
                 log.Type == Logger.EventType.MessageUpdated ||
                 log.Type == Logger.EventType.FileReceived)),
            cancellationToken: cancellationToken);

        SortedDictionary<long, LoggedMessage> messages = [];

        foreach (LogDbEntry entry in entries)
        {
            if ((ulong)entry.Snowflake >= ctx.Message.Id)
            {
                continue;
            }

            if (!messages.TryGetValue(entry.Snowflake, out LoggedMessage message))
            {
                messages[entry.Snowflake] = message = new LoggedMessage();
            }

            message.AuthorId = (ulong)entry.UserId;

            if (entry.Type == Logger.EventType.FileReceived)
            {
                if (TryGetAttachmentName(entry, out string filename))
                {
                    (message.Attachments ??= []).Add(filename);
                }
            }
            else if (!string.IsNullOrWhiteSpace(entry.Content))
            {
                message.Content = entry.Content;

                if (entry.Type == Logger.EventType.MessageUpdated)
                {
                    message.Content += " (edited)";
                }
            }
        }

        var builder = new StringBuilder();
        int messageCount = 0;
        int focusMessageCount = 0;

        foreach ((long snowflake, LoggedMessage message) in messages)
        {
            string content = message.Content?.Trim();

            if (message.Attachments is not null)
            {
                string attachments = $"[attachments: {string.Join(", ", message.Attachments)}]";
                content = string.IsNullOrEmpty(content) ? attachments : $"{content} {attachments}";
            }

            if (string.IsNullOrEmpty(content) || message.AuthorId == 0)
            {
                continue;
            }

            content = content.ReplaceLineEndings("  ").Trim();

            if (content.Length > 200 && (await botsTask).Contains(message.AuthorId))
            {
                content = content.TruncateWithDotDotDot(200);
            }

            messageCount++;

            bool isFocused = focusUserId.HasValue && message.AuthorId == focusUserId.Value;

            if (isFocused)
            {
                focusMessageCount++;
                builder.Append(FocusMarker).Append(' ');
            }

            builder.Append('[').Append(SnowflakeUtils.FromSnowflake((ulong)snowflake).ToISODateTime()).Append("] ");
            builder.Append(GetDisplayName(ctx, message.AuthorId)).Append(": ");
            builder.AppendLine(content);

            if (builder.Length > MaxTranscriptLength)
            {
                break;
            }
        }

        return (builder.ToString(), messageCount, focusMessageCount);
    }

    private sealed class LoggedMessage
    {
        public ulong AuthorId;
        public string Content;
        public List<string> Attachments;
    }

    private static bool TryGetAttachmentName(LogDbEntry entry, out string filename)
    {
        filename = null;

        if (string.IsNullOrEmpty(entry.ExtraContentJson))
        {
            return false;
        }

        try
        {
            filename = JsonSerializer.Deserialize<Logger.AttachmentModel>(entry.ExtraContentJson)?.Filename;
        }
        catch (JsonException)
        {
            return false;
        }

        return !string.IsNullOrEmpty(filename);
    }

    private static string GetDisplayName(CommandContext ctx, ulong userId) =>
        ctx.Guild.GetUser(userId)?.GetName() ?? ctx.Discord.GetUser(userId)?.GetName() ?? userId.ToString();

    /// <summary>Splits the argument into the duration part and the optional person name, e.g. "2 hours from someone".</summary>
    /// <param name="mentionedUserIds">Ids of users mentioned in the message - their mention tags are stripped from the argument.</param>
    public static (string Duration, string User) SplitArguments(string argument, IEnumerable<ulong> mentionedUserIds = null)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return (null, null);
        }

        bool hasMention = false;

        foreach (ulong id in mentionedUserIds ?? [])
        {
            foreach (string mention in (string[])[MentionUtils.MentionUser(id), $"<@!{id}>"])
            {
                if (argument.Contains(mention, StringComparison.Ordinal))
                {
                    hasMention = true;
                    argument = argument.Replace(mention, " ", StringComparison.Ordinal);
                }
            }
        }

        string user = null;
        string[] parts = argument.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        int keywordIndex = Array.FindIndex(parts, p =>
            p.Equals("from", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("by", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("about", StringComparison.OrdinalIgnoreCase));

        if (keywordIndex >= 0)
        {
            if (hasMention)
            {
                parts = parts.Where((_, i) => i != keywordIndex).ToArray();
            }
            else
            {
                string after = string.Join(' ', parts.Skip(keywordIndex + 1));

                if (!string.IsNullOrEmpty(after))
                {
                    user = after;
                    parts = parts.Take(keywordIndex).ToArray();
                }
            }
        }

        return (string.Join(' ', parts), user);
    }

    private static async Task<IUser> ResolveUserAsync(CommandContext ctx, string pattern)
    {
        if (ulong.TryParse(pattern, out ulong userId) && ctx.Guild.GetUser(userId) is { } userById)
        {
            return userById;
        }

        return Choose(ctx.Channel.Users) ?? Choose(ctx.Guild.Users);

        SocketGuildUser Choose(IEnumerable<SocketGuildUser> users)
        {
            SocketGuildUser[] candidates = users
                .Where(u => Matches(u, static (name, p) => name.Equals(p, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (candidates.Length == 0)
            {
                candidates = users
                    .Where(u => Matches(u, static (name, p) => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }

            if (candidates.Length > 1)
            {
                candidates = [.. candidates.Where(u => !u.IsBot)];
            }

            return candidates.FirstOrDefault();
        }

        bool Matches(SocketGuildUser user, Func<string, string, bool> comparison)
        {
            return
                (user.Nickname is { } nickname && comparison(nickname, pattern)) ||
                (user.GlobalName is { } globalName && comparison(globalName, pattern)) ||
                comparison(user.Username, pattern);
        }
    }

    /// <summary>Formats token counts like 123742 as "123.7k".</summary>
    public static string FormatTokenCount(long count)
    {
        if (count < 1_000)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        if (count < 1_000_000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{count / 1_000d:0.#}k");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{count / 1_000_000d:0.##}M");
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
