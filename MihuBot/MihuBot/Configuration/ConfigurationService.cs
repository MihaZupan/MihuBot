using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MihuBot.Configuration
{
    public sealed class ConfigurationService : IConfigurationService
    {
        private readonly Dictionary<ulong, Dictionary<string, string>> _configuration;
        private readonly Dictionary<string, string> _globalConfiguration;
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, Dictionary<string, string>>> _store;
        private readonly SynchronizedLocalJsonStore<Dictionary<string, string>> _globalStore;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        public ConfigurationService()
        {
            _store = new SynchronizedLocalJsonStore<Dictionary<ulong, Dictionary<string, string>>>("Configuration.json",
                dictionary =>
                {
                    var configuration = new Dictionary<ulong, Dictionary<string, string>>();
                    foreach (var entry in dictionary)
                    {
                        configuration.Add(entry.Key, new Dictionary<string, string>(entry.Value, StringComparer.OrdinalIgnoreCase));
                    }
                    return configuration;
                });

            _globalStore = new SynchronizedLocalJsonStore<Dictionary<string, string>>("GlobalConfiguration.json",
                dictionary => new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase));

            _configuration = _store.DangerousGetValue();
            _globalConfiguration = _globalStore.DangerousGetValue();
        }

        public void Set(ulong? context, string key, string value)
        {
            _store.Enter();
            try
            {
                _lock.EnterWriteLock();

                Dictionary<string, string> configuration;
                if (context.HasValue)
                {
                    if (!_configuration.TryGetValue(context.Value, out configuration))
                        configuration = _configuration[context.Value] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    configuration = _globalConfiguration;
                }

                configuration[key] = value;
            }
            finally
            {
                _lock.ExitWriteLock();
                _store.Exit();
            }
        }

        public bool Remove(ulong? context, string key)
        {
            _store.Enter();
            try
            {
                _lock.EnterWriteLock();

                Dictionary<string, string> configuration;
                if (context.HasValue)
                {
                    if (!_configuration.TryGetValue(context.Value, out configuration))
                        return false;
                }
                else
                {
                    configuration = _globalConfiguration;
                }

                if (!configuration.Remove(key))
                    return false;

                if (configuration.Count == 0 && context.HasValue)
                {
                    _configuration.Remove(context.Value);
                }

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
                _store.Exit();
            }
        }

        public bool TryGet(ulong? context, string key, out string value)
        {
            _lock.EnterReadLock();
            try
            {
                Dictionary<string, string> configuration;
                if (context.HasValue)
                {
                    if (!_configuration.TryGetValue(context.Value, out configuration))
                    {
                        value = null;
                        return false;
                    }
                }
                else
                {
                    configuration = _globalConfiguration;
                }

                return configuration.TryGetValue(key, out value);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }
}
