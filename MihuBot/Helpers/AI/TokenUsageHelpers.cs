using System.Globalization;

namespace MihuBot.Helpers.AI;

public static class TokenUsageHelpers
{
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
