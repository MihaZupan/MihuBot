using Microsoft.Extensions.AI;
using MihuBot.Discord.Commands;

namespace MihuBot.Tests.Discord;

public sealed class AIOverviewCommandTests
{
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