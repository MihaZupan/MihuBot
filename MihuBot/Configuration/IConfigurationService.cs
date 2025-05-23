﻿namespace MihuBot.Configuration;

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
}
