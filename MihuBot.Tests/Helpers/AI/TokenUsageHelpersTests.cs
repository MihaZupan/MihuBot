using Microsoft.Extensions.AI;
using MihuBot.Helpers.AI;

namespace MihuBot.Tests.Helpers.AI;

public sealed class TokenUsageHelpersTests
{
    [Theory]
    [InlineData("gpt-6-astra", "1.5")]
    [InlineData("GPT-6-ASTRA", "1.5")]
    [InlineData("gpt-6-luna", "0.015")]
    [InlineData("gpt-6-sol", "0.3")]
    [InlineData("gpt-6-luna-2026-09-22", "0.015")]
    [InlineData("GPT-6-Luna-2026-09-22", "0.015")]
    public void EstimateCostUsd_UsesModelRates(string model, string expected)
    {
        var usage = new UsageDetails { InputTokenCount = 100_000, OutputTokenCount = 10_000 };

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), TokenUsageHelpers.EstimateCostUsd(model, usage));
    }

    [Fact]
    public void EstimateCostUsd_DiscountsCacheWithoutDoubleCountingReasoning()
    {
        var usage = new UsageDetails
        {
            InputTokenCount = 100_000,
            CachedInputTokenCount = 80_000,
            OutputTokenCount = 10_000,
            ReasoningTokenCount = 9_000,
        };

        Assert.Equal(0.0078m, TokenUsageHelpers.EstimateCostUsd("gpt-6-luna", usage));
    }

    [Theory]
    [InlineData("gpt-6-luna", 272_000, "0.0322")]
    [InlineData("gpt-6-luna", 272_001, "0.0619002")]
    [InlineData("gpt-6-sol", 272_000, "0.644")]
    [InlineData("gpt-6-sol", 272_001, "1.238004")]
    [InlineData("gpt-6-astra", 272_000, "3.22")]
    [InlineData("gpt-6-astra", 272_001, "6.19002")]
    public void EstimateCostUsd_AppliesLongContextThreshold(string model, long inputTokens, string expected)
    {
        var usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = 10_000 };

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), TokenUsageHelpers.EstimateCostUsd(model, usage));
    }

    [Theory]
    [InlineData("gpt-6-luna", "0.0315")]
    [InlineData("gpt-6-sol", "0.63")]
    [InlineData("gpt-6-astra", "3.15")]
    public void EstimateCostUsd_LongContextIncludesCachedTokens(string model, string expected)
    {
        var usage = new UsageDetails { InputTokenCount = 300_000, CachedInputTokenCount = 200_000, OutputTokenCount = 10_000 };

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), TokenUsageHelpers.EstimateCostUsd(model, usage));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("gpt-6-luna-custom")]
    [InlineData("gpt-6-luna-2026-99-99")]
    public void EstimateCostUsd_UnknownModelIsUnavailable(string? model)
    {
        Assert.Null(TokenUsageHelpers.EstimateCostUsd(model, new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 }));
    }

    [Theory]
    [InlineData(null, 10L, null)]
    [InlineData(100L, null, null)]
    [InlineData(-1L, 10L, null)]
    [InlineData(100L, -1L, null)]
    [InlineData(100L, 10L, -1L)]
    [InlineData(100L, 10L, 101L)]
    public void EstimateCostUsd_IncompleteOrInvalidUsageIsUnavailable(long? input, long? output, long? cached)
    {
        Assert.Null(TokenUsageHelpers.EstimateCostUsd("gpt-6-luna", new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            CachedInputTokenCount = cached,
        }));
    }

    [Fact]
    public void EstimateCostUsd_MissingUsageIsUnavailable()
    {
        Assert.Null(TokenUsageHelpers.EstimateCostUsd("gpt-6-luna", null));
    }

    [Fact]
    public void EstimateCostUsd_AllKnownModelsHavePricing()
    {
        var usage = new UsageDetails { InputTokenCount = 100_000, CachedInputTokenCount = 50_000, OutputTokenCount = 10_000 };

        foreach (ModelInfo model in OpenAIService.AllModels)
        {
            Assert.True(model.InputUsdPerMillionTokens > 0);
            Assert.True(model.CachedInputUsdPerMillionTokens > 0);
            Assert.True(model.OutputUsdPerMillionTokens > 0);

            decimal expected = (0.05m * model.InputUsdPerMillionTokens) +
                (0.05m * model.CachedInputUsdPerMillionTokens) +
                (0.01m * model.OutputUsdPerMillionTokens);

            Assert.Equal(expected, TokenUsageHelpers.EstimateCostUsd(model.Name, usage));
        }
    }

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
