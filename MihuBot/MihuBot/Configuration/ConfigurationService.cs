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

        private void EnterWriteLock(ulong? context)
        {
            if (context.HasValue)
            {
                _store.Enter();
            }
            else
            {
                _globalStore.Enter();
            }

            try
            {
                _lock.EnterWriteLock();
            }
            catch
            {
                _lock.ExitWriteLock();
                throw;
            }
        }

        private void ExitWriteLock(ulong? context)
        {
            try
            {
                _lock.ExitWriteLock();
            }
            finally
            {
                if (context.HasValue)
                {
                    _store.Exit();
                }
                else
                {
                    _globalStore.Exit();
                }
            }
        }

        public void Set(ulong? context, string key, string value)
        {
            EnterWriteLock(context);
            try
            {
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
                ExitWriteLock(context);
            }
        }

        public bool Remove(ulong? context, string key)
        {
            EnterWriteLock(context);
            try
            {
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
                ExitWriteLock(context);
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
