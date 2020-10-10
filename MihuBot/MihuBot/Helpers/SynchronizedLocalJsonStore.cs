using Newtonsoft.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.Helpers
{
    public sealed class SynchronizedLocalJsonStore<T>
        where T : class, new()
    {
        private readonly string _jsonPath;
        private readonly T _value;
        private readonly SemaphoreSlim _asyncLock;

        public SynchronizedLocalJsonStore(string jsonPath)
        {
            _jsonPath = jsonPath;

            _value = File.Exists(_jsonPath)
                ? JsonConvert.DeserializeObject<T>(File.ReadAllText(_jsonPath))
                : new T();

            _asyncLock = new SemaphoreSlim(1, 1);
        }

        public async Task<T> EnterAsync()
        {
            await _asyncLock.WaitAsync();
            Logger.Instance.DebugLog($"Entered {_jsonPath}");
            return _value;
        }

        public void Exit()
        {
            Logger.Instance.DebugLog($"Exiting {_jsonPath}");
            File.WriteAllText(_jsonPath, JsonConvert.SerializeObject(_value, Formatting.Indented));
            _asyncLock.Release();
        }
    }
}
