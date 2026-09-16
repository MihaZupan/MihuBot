using Microsoft.Extensions.AI;
using MihuBot.DB;
using MihuBot.Discord.Commands;

namespace MihuBot.Tests.Discord;

public sealed class AIOverviewCommandTests
{
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
            ModelId = "gpt-5-mini-2025-08-07",
            Usage = new UsageDetails { InputTokenCount = 100_000, OutputTokenCount = 10_000 },
        };

        Assert.Equal("gpt-5-mini-2025-08-07 \u2022 100k tokens in, 10k out \u2022 ~$0.045 USD",
            AIOverviewCommand.FormatUsageFooter(response, "gpt-5"));
    }

    [Fact]
    public void FormatUsageFooter_FallsBackToClientModel()
    {
        Assert.Equal("gpt-5-mini \u2022 ~$0.00 USD",
            AIOverviewCommand.FormatUsageFooter(new ChatResponse(), "gpt-5-mini"));
    }

    [Fact]
    public void FormatUsageFooter_ReportsUnknownModelWithZeroCost()
    {
        Assert.Equal("Unknown model \u2022 ~$0.00 USD",
            AIOverviewCommand.FormatUsageFooter(new ChatResponse(), null!));
    }

    [Theory]
    [InlineData("gpt-5-mini", 1L, 0L, "~$0.00 USD")]
    [InlineData("gpt-5-mini", 0L, 0L, "~$0.00 USD")]
    [InlineData("gpt-5-mini", 100L, null, "~$0.00 USD")]
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