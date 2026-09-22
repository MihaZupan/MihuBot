using Microsoft.Extensions.AI;
using MihuBot.DB;
using MihuBot.Discord.Commands;

namespace MihuBot.Tests.Discord;

public sealed class AIOverviewCommandTests
{
    [Theory]
    [InlineData("", "", null)]
    [InlineData("2 hours from john", "2 hours from john", null)]
    [InlineData("123 2 hours", "2 hours", 123ul)]
    [InlineData("2 hours 123", "2 hours", 123ul)]
    [InlineData("123", "", 123ul)]
    [InlineData("2 hours 123 from <@456>", "2 hours from <@456>", 123ul)]
    [InlineData("2 hours from john 123", "2 hours from john", 123ul)]
    [InlineData("2 hours from 789", "2 hours from 789", null)]
    [InlineData("789 2 hours", "789 2 hours", null)]
    [InlineData("<#123> 2 hours", "<#123> 2 hours", null)]
    [InlineData("https://discord.com/channels/42/123 2h", "https://discord.com/channels/42/123 2h", null)]
    [InlineData("<https://discord.com/channels/42/123>", "<https://discord.com/channels/42/123>", null)]
    [InlineData("https://discord.com/channels/42/123/999", "https://discord.com/channels/42/123/999", null)]
    [InlineData("0", "0", null)]
    [InlineData("-123", "-123", null)]
    [InlineData("18446744073709551616", "18446744073709551616", null)]
    public void TryExtractChannelArgument_ExtractsOnlyGuildChannelId(string argument, string expectedArgument, ulong? expectedChannelId)
    {
        string[] arguments = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(AIOverviewCommand.TryExtractChannelArgument(arguments, id => id is 123 or 456, out string remainingArgument, out ulong? channelId));
        Assert.Equal(expectedArgument, remainingArgument);
        Assert.Equal(expectedChannelId, channelId);
    }

    [Theory]
    [InlineData("123 456")]
    [InlineData("123 2 hours 123")]
    public void TryExtractChannelArgument_RejectsRepeatedChannelIds(string argument)
    {
        string[] arguments = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.False(AIOverviewCommand.TryExtractChannelArgument(arguments, id => id is 123 or 456, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildTranscript_MarksDeletedMessagesWithoutLosingAuthorOrContent(bool deletionFirst)
    {
        var received = new LogDbEntry { Snowflake = 1, Type = Logger.EventType.MessageReceived, UserId = 42, Content = "Original message" };
        var deleted = new LogDbEntry { Snowflake = 1, Type = Logger.EventType.MessageDeleted };
        LogDbEntry[] entries = deletionFirst ? [deleted, received] : [received, deleted];

        var (transcript, messageCount, focusMessageCount) = AIOverviewCommand.BuildTranscript(entries, 100, 42, [], id => $"User {id}");

        Assert.StartsWith(">> [", transcript);
        Assert.EndsWith($"User 42 (deleted): Original message{Environment.NewLine}", transcript);
        Assert.Equal(1, messageCount);
        Assert.Equal(1, focusMessageCount);
    }

    [Fact]
    public void BuildTranscript_PreservesEditsAndAttachmentsOnDeletedMessages()
    {
        LogDbEntry[] entries =
        [
            new() { Snowflake = 1, Type = Logger.EventType.MessageReceived, UserId = 42, Content = "Original message" },
            new() { Snowflake = 1, Type = Logger.EventType.MessageUpdated, UserId = 42, Content = "Updated message" },
            new() { Snowflake = 1, Type = Logger.EventType.FileReceived, UserId = 42, ExtraContentJson = """{"Filename":"example.txt"}""" },
            new() { Snowflake = 1, Type = Logger.EventType.MessageDeleted },
            new() { Snowflake = 2, Type = Logger.EventType.FileReceived, UserId = 43, ExtraContentJson = """{"Filename":"photo.png"}""" },
            new() { Snowflake = 2, Type = Logger.EventType.MessageDeleted },
        ];

        var (transcript, messageCount, _) = AIOverviewCommand.BuildTranscript(entries, 100, null, [], id => $"User {id}");

        Assert.Contains($"User 42 (deleted): Updated message (edited) [attachments: example.txt]{Environment.NewLine}", transcript);
        Assert.EndsWith($"User 43 (deleted): [attachments: photo.png]{Environment.NewLine}", transcript);
        Assert.DoesNotContain("Original message", transcript);
        Assert.Equal(2, messageCount);
    }

    [Fact]
    public void BuildTranscript_KeepsDeletionNoteAfterTruncatingBotContent()
    {
        LogDbEntry[] entries =
        [
            new() { Snowflake = 1, Type = Logger.EventType.MessageReceived, UserId = 42, Content = new string('x', 300) },
            new() { Snowflake = 1, Type = Logger.EventType.MessageDeleted },
        ];

        var (transcript, messageCount, _) = AIOverviewCommand.BuildTranscript(entries, 100, null, [42], _ => "Bot");

        Assert.Contains("Bot (deleted): ", transcript);
        Assert.DoesNotContain(new string('x', 201), transcript);
        Assert.Equal(1, messageCount);
    }

    [Fact]
    public void BuildTranscript_IgnoresOrphanDeletionsAndMessagesAtOrAfterCommand()
    {
        LogDbEntry[] entries =
        [
            new() { Snowflake = 1, Type = Logger.EventType.MessageDeleted },
            new() { Snowflake = 2, Type = Logger.EventType.MessageReceived, UserId = 42, Content = "Still here" },
            new() { Snowflake = 3, Type = Logger.EventType.MessageReceived, UserId = 42, Content = "Command" },
            new() { Snowflake = 4, Type = Logger.EventType.MessageReceived, UserId = 42, Content = "Later message" },
        ];

        var (transcript, messageCount, focusMessageCount) = AIOverviewCommand.BuildTranscript(entries, 3, null, [], _ => "User");

        Assert.EndsWith($"User: Still here{Environment.NewLine}", transcript);
        Assert.DoesNotContain("(deleted)", transcript);
        Assert.DoesNotContain("Command", transcript);
        Assert.DoesNotContain("Later message", transcript);
        Assert.Equal(1, messageCount);
        Assert.Equal(0, focusMessageCount);
    }

    [Fact]
    public void FormatUsageFooter_IncludesResponseModelTokensAndCost()
    {
        var response = new ChatResponse
        {
            ModelId = "gpt-6-luna-2026-09-22",
            Usage = new UsageDetails { InputTokenCount = 100_000, OutputTokenCount = 10_000 },
        };

        Assert.Equal("100k tokens in, 10k out \u2022 gpt-6-luna \u2022 ~$0.02 USD",
            AIOverviewCommand.FormatUsageFooter(response, "gpt-6-sol"));
    }

    [Theory]
    [InlineData("gpt-6-luna", "gpt-6-luna")]
    [InlineData("gpt-6-luna-2026-09-22", "gpt-6-luna")]
    [InlineData("gpt-6-astra", "gpt-6-astra")]
    [InlineData("gpt-6-luna-2026-02-30", "gpt-6-luna-2026-02-30")]
    public void FormatUsageFooter_FallsBackToClientModel(string model, string expectedModel)
    {
        Assert.Equal($"{expectedModel} \u2022 ~$0.00 USD",
            AIOverviewCommand.FormatUsageFooter(new ChatResponse(), model));
    }

    [Fact]
    public void FormatUsageFooter_ReportsUnknownModelWithZeroCost()
    {
        Assert.Equal("Unknown model \u2022 ~$0.00 USD",
            AIOverviewCommand.FormatUsageFooter(new ChatResponse(), null!));
    }

    [Theory]
    [InlineData("gpt-6-luna", 1L, 0L, "~$0.00 USD")]
    [InlineData("gpt-6-luna", 0L, 0L, "~$0.00 USD")]
    [InlineData("gpt-6-luna", 100L, null, "~$0.00 USD")]
    [InlineData("gpt-6-luna", 133_600L, 0L, "~$0.01 USD")]
    [InlineData("unknown", 100L, 10L, "~$0.00 USD")]
    public void FormatUsageFooter_HandlesSmallCostsAndUnavailableEstimates(string model, long input, long? output, string expectedCost)
    {
        var response = new ChatResponse
        {
            ModelId = model,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        };

        Assert.EndsWith($" \u2022 {expectedCost}", AIOverviewCommand.FormatUsageFooter(response, model));
    }

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("30 min", 30)]
    [InlineData("45 minutes", 45)]
    [InlineData("2h", 120)]
    [InlineData("1.5h", 90)]
    [InlineData("1h 30m", 90)]
    [InlineData("1 hour 30 minutes", 90)]
    [InlineData("an hour", 60)]
    [InlineData("1d", 1440)]
    [InlineData("90s", 1.5)]
    public void TryParseDuration_ParsesValidDurations(string argument, double expectedMinutes)
    {
        Assert.True(AIOverviewCommand.TryParseDuration(argument, out TimeSpan duration));
        Assert.Equal(expectedMinutes, duration.TotalMinutes, 1);
    }

    [Theory]
    [InlineData("", "", null)]
    [InlineData("2 hours", "2 hours", null)]
    [InlineData("<@1234>", "", null)]
    [InlineData("<@!1234> 2 hours", "2 hours", null)]
    [InlineData("2 hours from <@1234>", "2 hours", null)]
    [InlineData("from <@1234> 2 hours", "2 hours", null)]
    [InlineData("2 hours from john", "2 hours", "john")]
    [InlineData("30m by john doe", "30m", "john doe")]
    [InlineData("from john", "", "john")]
    public void SplitArguments_SeparatesDurationAndUser(string argument, string expectedDuration, string? expectedUser)
    {
        (string duration, string user) = AIOverviewCommand.SplitArguments(argument, [1234ul]);

        Assert.Equal(expectedDuration, duration ?? "");
        Assert.Equal(expectedUser, user);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("30")]
    public void TryParseDuration_RejectsInvalidDurations(string argument)
    {
        Assert.False(AIOverviewCommand.TryParseDuration(argument, out _));
    }
}