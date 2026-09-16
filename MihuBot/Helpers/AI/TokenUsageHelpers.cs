using Microsoft.Extensions.AI;
using System.Globalization;

#nullable enable

namespace MihuBot.Helpers.AI;

public static class TokenUsageHelpers
{
    /// <summary>Estimates standard text-token cost in USD; null means pricing or usage is unavailable.</summary>
    public static decimal? EstimateCostUsd(string? model, UsageDetails? usage)
    {
        if (model is null || usage is not { InputTokenCount: >= 0, OutputTokenCount: >= 0 })
        {
            return null;
        }

        // Responses may identify a dated snapshot instead of the configured model alias.
        if (model.Length > 11 && model[^11] == '-' &&
            DateOnly.TryParseExact(model.AsSpan(model.Length - 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            model = model[..^11];
        }

        ModelInfo? modelInfo = OpenAIService.AllModels.FirstOrDefault(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase));

        long inputTokens = usage.InputTokenCount.Value;
        long outputTokens = usage.OutputTokenCount.Value;
        long cachedTokens = usage.CachedInputTokenCount ?? 0;

        if (modelInfo is null || cachedTokens < 0 || cachedTokens > inputTokens)
        {
            return null;
        }

        decimal input = modelInfo.InputUsdPerMillionTokens;
        decimal cachedInput = modelInfo.CachedInputUsdPerMillionTokens;
        decimal output = modelInfo.OutputUsdPerMillionTokens;

        if (modelInfo.LongContextThreshold is { } threshold && inputTokens > threshold)
        {
            input *= 2;
            cachedInput *= 2;
            output *= 1.5m;
        }

        return (((inputTokens - cachedTokens) * input) + (cachedTokens * cachedInput) + (outputTokens * output)) / 1_000_000m;
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
}
