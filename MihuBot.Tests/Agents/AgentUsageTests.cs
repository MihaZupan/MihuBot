using GitHub.Copilot;
using MihuBot.Agents;

namespace MihuBot.Tests.Agents;

public sealed class AgentUsageTests
{
    [Fact]
    public void UsageIncludesSubagentsAndUsesEachModelsCachePricing()
    {
        var usage = new AgentUsage();
        var parent = Event("gpt-6.1-sol-2026-09-29", 100_000, 10_000, cached: 60_000);
        parent.Data.ReasoningTokens = 9_000;
        usage.Add(parent);
        usage.Add(Event("gpt-6-luna", 200_000, 5_000, cached: 100_000, agentId: "child"));
        usage.Add(parent);

        Assert.Equal("300k tokens in, 15k out • gpt-6-luna, gpt-6.1-sol • ~$0.20 USD", usage.FormatFooter());
    }

    [Fact]
    public void LongContextThresholdIsAppliedPerCallNotToAccumulatedInput()
    {
        var usage = new AgentUsage();
        usage.Add(Event("gpt-6.1-sol", 200_000, 0, callId: "first"));
        usage.Add(Event("gpt-6.1-sol", 200_000, 0, callId: "second"));

        Assert.Equal("400k tokens in, 0 out • gpt-6.1-sol • ~$0.80 USD", usage.FormatFooter());
    }

    [Fact]
    public void UnknownModelDoesNotReportPartialCostAsACompleteEstimate()
    {
        var usage = new AgentUsage();
        usage.Add(Event("gpt-6.1-sol", 100_000, 10_000));
        usage.Add(Event("unpriced-model", 1_000, 100, agentId: "child"));
        string footer = usage.FormatFooter();

        Assert.Contains("101k tokens in, 10.1k out", footer, StringComparison.Ordinal);
        Assert.Contains("USD estimate unavailable", footer, StringComparison.Ordinal);
        Assert.DoesNotContain("~$", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTokenCountsRemainUnknownRatherThanZero()
    {
        var usage = new AgentUsage();
        Assert.Null(usage.FormatFooter());
        usage.Add(Event("gpt-6.1-sol", null, 10));
        usage.Add(Event("gpt-6.1-sol", 100, 20, callId: "second"));

        Assert.Equal("unknown tokens in, 30 out • gpt-6.1-sol • USD estimate unavailable", usage.FormatFooter());
    }

    [Fact]
    public void EntirelyMissingUsageAndInvalidCacheCountsAreUnavailable()
    {
        var usage = new AgentUsage();
        usage.Add(Event("gpt-6.1-sol", null, null));
        Assert.StartsWith("Token counts unavailable", usage.FormatFooter(), StringComparison.Ordinal);
        Assert.Contains("USD estimate unavailable", usage.FormatFooter(), StringComparison.Ordinal);

        var invalidCache = new AgentUsage();
        invalidCache.Add(Event("gpt-6.1-sol", 100, 10, cached: 200));
        Assert.Contains("USD estimate unavailable", invalidCache.FormatFooter(), StringComparison.Ordinal);
    }

    [Fact]
    public void UsageDeduplicatesApiCallsAndFallsBackToEventIds()
    {
        var usage = new AgentUsage();
        usage.Add(Event("gpt-6.1-sol", 100, 10));
        usage.Add(Event("gpt-6.1-sol", 100, 10));
        var withoutCallId = Event("gpt-6.1-sol", 100, 10, callId: null);
        usage.Add(withoutCallId);
        usage.Add(withoutCallId);

        Assert.StartsWith("200 tokens in, 20 out", usage.FormatFooter(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressAndFinalSummaryRetainTheSameUsage()
    {
        var progress = new AgentProgress();
        Assert.Null(progress.RenderUsage());
        progress.OnEvent(Event("gpt-6.1-sol", 100_000, 10_000, cached: 60_000));
        string footer = progress.Render(TimeSpan.FromSeconds(3)).Footer!.Value.Text;
        var summary = progress.RenderUsage();

        Assert.NotNull(summary);
        Assert.Equal("Agent usage", summary.Title);
        Assert.Equal(footer, summary.Footer!.Value.Text);
        Assert.Equal("100k tokens in, 10k out • gpt-6.1-sol • ~$0.19 USD", footer);
        Assert.Empty(summary.Fields);
    }

    internal static AssistantUsageEvent Event(string model, long? input, long? output, long? cached = null, string? agentId = null, string? callId = "call") => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        Data = new AssistantUsageData
        {
            Model = model,
            ApiCallId = callId,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cached
        }
    };
}
