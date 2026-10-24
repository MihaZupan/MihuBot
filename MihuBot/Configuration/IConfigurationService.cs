using System.Globalization;

namespace MihuBot.Configuration;

public interface IConfigurationService
{
    void Set(ulong? context, string key, string value);

    bool Remove(ulong? context, string key);

    string Get(ulong? context, string key)
    {
        if (TryGet(context, key, out string value))
        {
            return value;
        }

        throw new KeyNotFoundException($"{context} - {key}");
    }

    bool TryGet(ulong? context, string key, out string value);

    bool GetOrDefault(ulong? context, string key, bool defaultValue)
    {
        if (TryGet(context, key, out string str) && bool.TryParse(str, out bool value))
        {
            return value;
        }

        return defaultValue;
    }

    int GetInt32(ulong? context, string key, int defaultValue)
    {
        if (!TryGet(context, key, out string str))
        {
            return defaultValue;
        }

        return int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidOperationException($"Configuration setting '{key}' must be an integer.");
    }

    int GetOrDefault(ulong? context, string key, int defaultValue)
    {
        if (TryGet(context, key, out string str) && int.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        return defaultValue;
    }

    float GetOrDefault(ulong? context, string key, float defaultValue)
    {
        if (TryGet(context, key, out string str) && float.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out float value))
        {
            return value;
        }

        return defaultValue;
    }

    double GetOrDefault(ulong? context, string key, double defaultValue)
    {
        if (TryGet(context, key, out string str) && double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        return defaultValue;
    }
}
