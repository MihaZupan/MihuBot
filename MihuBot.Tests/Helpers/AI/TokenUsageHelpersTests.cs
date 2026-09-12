using MihuBot.Helpers.AI;

namespace MihuBot.Tests.Helpers.AI;

public sealed class TokenUsageHelpersTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(1234, "1.2k")]
    [InlineData(123742, "123.7k")]
    [InlineData(999999, "1000k")]
    [InlineData(1500000, "1.5M")]
    public void FormatTokenCount_FormatsCounts(long count, string expected)
    {
        Assert.Equal(expected, TokenUsageHelpers.FormatTokenCount(count));
    }
}
