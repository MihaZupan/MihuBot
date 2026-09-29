using GitHub.Copilot;
using Microsoft.Extensions.AI;
using MihuBot.Helpers.AI;

namespace MihuBot.Agents;

internal sealed class AgentUsage
{
    private readonly HashSet<(string AgentId, string CallId)> _seenCalls = [];
    private readonly HashSet<string> _models = new(StringComparer.OrdinalIgnoreCase);
    private long _inputTokens;
    private long _outputTokens;
    private decimal _estimatedCost;
    private bool _inputComplete = true;
    private bool _outputComplete = true;
    private bool _costComplete = true;

    public void Add(AssistantUsageEvent evt)
    {
        AssistantUsageData usage = evt.Data;
        string callId = usage.ApiCallId ?? usage.ProviderCallId ?? (evt.Id != Guid.Empty ? evt.Id.ToString() : null);

        if (callId is not null && !_seenCalls.Add((evt.AgentId, callId)))
        {
            return;
        }

        _models.Add(TokenUsageHelpers.WithoutSnapshotDate(string.IsNullOrWhiteSpace(usage.Model) ? "Unknown model" : usage.Model));

        if (usage.InputTokens is >= 0)
        {
            _inputTokens += usage.InputTokens.Value;
        }
        else
        {
            _inputComplete = false;
        }

        if (usage.OutputTokens is >= 0)
        {
            _outputTokens += usage.OutputTokens.Value;
        }
        else
        {
            _outputComplete = false;
        }

        decimal? cost = TokenUsageHelpers.EstimateCostUsd(usage.Model, new UsageDetails
        {
            InputTokenCount = usage.InputTokens,
            OutputTokenCount = usage.OutputTokens,
            CachedInputTokenCount = usage.CacheReadTokens
        });

        if (cost is { } knownCost)
        {
            // Price each call independently: accumulated input is not a single long-context request.
            _estimatedCost += knownCost;
        }
        else
        {
            _costComplete = false;
        }
    }

    public string FormatFooter()
    {
        if (_models.Count == 0)
        {
            return null;
        }

        string models = string.Join(", ", _models.Order(StringComparer.OrdinalIgnoreCase)).TruncateWithDotDotDot(100);
        string footer = TokenUsageHelpers.FormatUsageFooter(models,
            _inputComplete ? _inputTokens : null,
            _outputComplete ? _outputTokens : null,
            _costComplete ? _estimatedCost : null);

        if (!_inputComplete && !_outputComplete)
        {
            footer = $"Token counts unavailable • {footer}";
        }

        return footer;
    }
}
