using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace MihuBot.Configuration;

public sealed class ConfigurationService : IConfigurationService, IDisposable
{
    private readonly SynchronizedLocalJsonStore<Dictionary<ulong, Dictionary<string, string>>> _store;
    private readonly SynchronizedLocalJsonStore<Dictionary<string, string>> _globalStore;
    private readonly PhysicalFileProvider _fileProvider;
    private readonly IDisposable _configurationSubscription;
    private readonly IDisposable _globalConfigurationSubscription;
    private readonly Lock _reloadLock = new();
    private bool _disposed;

    // Logging providers depend on configuration, so reload errors must bypass ILogger.
    public ConfigurationService()
        : this(Constants.StateDirectory)
    {
    }

    internal ConfigurationService(string directory, Action<string> logError = null)
    {
        logError ??= Console.Error.WriteLine;
        directory = Path.GetFullPath(directory);
        _store = new SynchronizedLocalJsonStore<Dictionary<ulong, Dictionary<string, string>>>(Path.Combine(directory, "Configuration.json"),
            (_, dictionary) =>
            {
                var configuration = new Dictionary<ulong, Dictionary<string, string>>();

                foreach (var entry in dictionary)
                {
                    configuration.Add(entry.Key, new Dictionary<string, string>(entry.Value, StringComparer.OrdinalIgnoreCase));
                }

                return configuration;
            });

        _globalStore = new SynchronizedLocalJsonStore<Dictionary<string, string>>(Path.Combine(directory, "GlobalConfiguration.json"),
            (_, dictionary) => new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase));

        Directory.CreateDirectory(directory);
        _fileProvider = new PhysicalFileProvider(directory);
        _configurationSubscription = Watch("Configuration.json", _store.Reload);
        _globalConfigurationSubscription = Watch("GlobalConfiguration.json", _globalStore.Reload);

        IDisposable Watch(string name, Action reload)
        {
            IDisposable subscription = ChangeToken.OnChange(() => _fileProvider.Watch(name), () =>
            {
                // Editors may emit a change before they have finished writing the file.
                Thread.Sleep(250);
                Reload();
            });

            // Pick up edits made between loading the store and subscribing to changes.
            if (File.Exists(Path.Combine(directory, name)))
            {
                Reload();
            }

            return subscription;

            void Reload()
            {
                lock (_reloadLock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    try
                    {
                        reload();
                    }
                    catch (Exception ex)
                    {
                        logError($"Could not reload {name}; keeping the previous configuration. Exception: {ex}");
                    }
                }
            }
        }
    }

    public void Set(ulong? context, string key, string value)
    {
        if (context.HasValue)
        {
            _store.Modify(configuration =>
            {
                Dictionary<string, string> values = CollectionsMarshal.GetValueRefOrAddDefault(configuration, context.Value, out _) ??= new(StringComparer.OrdinalIgnoreCase);

                values[key] = value;
            });
        }
        else
        {
            _globalStore.Modify(configuration => configuration[key] = value);
        }
    }

    public bool Remove(ulong? context, string key)
    {
        bool removed = false;

        if (context.HasValue)
        {
            _store.Modify(configuration =>
            {
                if (!configuration.TryGetValue(context.Value, out var values))
                {
                    return;
                }

                removed = values.Remove(key);

                if (removed && values.Count == 0)
                {
                    configuration.Remove(context.Value);
                }
            });
        }
        else
        {
            _globalStore.Modify(configuration => removed = configuration.Remove(key));
        }

        return removed;
    }

    public bool TryGet(ulong? context, string key, out string value)
    {
        string result = null;
        bool found = context.HasValue
            ? _store.Query(configuration => configuration.TryGetValue(context.Value, out var values) && values.TryGetValue(key, out result))
            : _globalStore.Query(configuration => configuration.TryGetValue(key, out result));

        value = result;
        return found;
    }

    public void Dispose()
    {
        lock (_reloadLock)
        {
            _disposed = true;
        }

        _configurationSubscription.Dispose();
        _globalConfigurationSubscription.Dispose();
        _fileProvider.Dispose();
    }
}
