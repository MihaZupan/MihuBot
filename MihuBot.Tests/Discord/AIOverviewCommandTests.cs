using MihuBot.Discord.Commands;

namespace MihuBot.Tests.Discord;

public sealed class AIOverviewCommandTests
{
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