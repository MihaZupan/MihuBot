using MihuBot.Configuration;

namespace MihuBot.Tests.Configuration;

internal sealed class TestConfigurationService : IConfigurationService
{
    private readonly Dictionary<(ulong? Context, string Key), string> _values = [];

    public void Set(ulong? context, string key, string value) =>
        _values[(context, key.ToUpperInvariant())] = value;

    public bool Remove(ulong? context, string key) =>
        _values.Remove((context, key.ToUpperInvariant()));

    public bool TryGet(ulong? context, string key, out string value) =>
        _values.TryGetValue((context, key.ToUpperInvariant()), out value!);
}
