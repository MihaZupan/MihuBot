using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MihuBot.Configuration
{
    public sealed class ConfigurationService : IConfigurationService
    {
        private readonly Dictionary<ulong?, Dictionary<string, string>> _configuration;
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong?, Dictionary<string, string>>> _store;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        public ConfigurationService()
        {
            _store = new SynchronizedLocalJsonStore<Dictionary<ulong?, Dictionary<string, string>>>("Configuration.json",
                dictionary =>
                {
                    var configuration = new Dictionary<ulong?, Dictionary<string, string>>();
                    foreach (var entry in dictionary)
                    {
                        configuration.Add(entry.Key, new Dictionary<string, string>(entry.Value, StringComparer.OrdinalIgnoreCase));
                    }
                    return configuration;
                });

            _configuration = _store.DangerousGetValue();
        }

        public void Set(ulong? context, string key, string value)
        {
            _store.Enter();
            try
            {
                _lock.EnterWriteLock();

                if (!_configuration.TryGetValue(context, out Dictionary<string, string> configuration))
                    configuration = _configuration[context] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                if (!_configuration.TryGetValue(context, out Dictionary<string, string> configuration))
                    return false;

                if (!configuration.Remove(key))
                    return false;

                if (configuration.Count == 0)
                {
                    _configuration.Remove(context);
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
                value = null;
                return _configuration.TryGetValue(context, out Dictionary<string, string> configuration)
                    && configuration.TryGetValue(key, out value);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }
}
