using Newtonsoft.Json;

namespace MihuBot.Helpers;

public sealed class SynchronizedLocalJsonStore<T>
    where T : class, new()
{
    private readonly string _jsonPath;
    private readonly T _value;
    private readonly SemaphoreSlim _asyncLock;
    private string _previousJson;

    public SynchronizedLocalJsonStore(string jsonPath, Func<SynchronizedLocalJsonStore<T>, T, T> init = null)
    {
        _jsonPath = $"{Constants.StateDirectory}/{jsonPath}";

        if (File.Exists(_jsonPath))
        {
            _previousJson = File.ReadAllText(_jsonPath);
            _value = JsonConvert.DeserializeObject<T>(_previousJson);
        }
        else
        {
            _previousJson = string.Empty;
            _value = new T();
        }

        _asyncLock = new SemaphoreSlim(1, 1);

        if (init != null)
        {
            _value = init(this, _value);
        }
    }

    public T DangerousGetValue() => _value;

    public async ValueTask<TResult> QueryAsync<TResult>(Func<T, TResult> selector)
    {
        await _asyncLock.WaitAsync();
        try
        {
            return selector(_value);
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async ValueTask QueryAsync(Action<T> action)
    {
        await _asyncLock.WaitAsync();
        try
        {
            action(_value);
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public T Enter()
    {
        _asyncLock.Wait();
        // Logger.DebugLog($"Entered {_jsonPath}");
        return _value;
    }

    public async ValueTask<T> EnterAsync()
    {
        await _asyncLock.WaitAsync();
        // Logger.DebugLog($"Entered {_jsonPath}");
        return _value;
    }

    public void Exit()
    {
        string newJson = JsonConvert.SerializeObject(_value, Formatting.Indented);

        if (newJson == _previousJson)
        {
            // Logger.DebugLog($"Exiting {_jsonPath}, no changes");
        }
        else
        {
            // Logger.DebugLog($"Exiting {_jsonPath}, saving changes");
            _previousJson = newJson;
            File.WriteAllText(_jsonPath, newJson);
        }

        _asyncLock.Release();
    }
}
