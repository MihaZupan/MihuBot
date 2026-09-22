using Newtonsoft.Json;

namespace MihuBot.Helpers;

public sealed class SynchronizedLocalJsonStore<T>
    where T : class, new()
{
    private readonly string _jsonPath;
    private readonly Action<string, string> _replaceFile;
    private readonly Func<SynchronizedLocalJsonStore<T>, T, T> _init;
    private readonly SemaphoreSlim _asyncLock = new(1, 1);
    private T _value;
    private string _previousJson;

    public SynchronizedLocalJsonStore(string jsonPath, Func<SynchronizedLocalJsonStore<T>, T, T> init = null)
        : this(jsonPath, init, null)
    {
    }

    internal SynchronizedLocalJsonStore(string jsonPath, Func<SynchronizedLocalJsonStore<T>, T, T> init,
        Action<string, string> replaceFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        _jsonPath = Path.GetFullPath(Path.IsPathRooted(jsonPath) ? jsonPath : Path.Combine(Constants.StateDirectory, jsonPath));
        _replaceFile = replaceFile ?? ((source, destination) => File.Move(source, destination, overwrite: true));
        _init = init;
        string json;

        try
        {
            json = File.ReadAllText(_jsonPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            json = null;
        }

        _previousJson = json;
        _value = json is null ? new T() : JsonConvert.DeserializeObject<T>(json);

        if (init != null)
        {
            _value = init(this, _value);
        }
    }

    internal SynchronizedLocalJsonStore(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
        _previousJson = string.Empty;
    }

    public T DangerousGetValue() => _value;

    internal void Reload()
    {
        _asyncLock.Wait();

        try
        {
            string json = File.ReadAllText(_jsonPath);

            if (json == _previousJson)
            {
                return;
            }

            T value = JsonConvert.DeserializeObject<T>(json)
                ?? throw new JsonSerializationException("The JSON store must not be null or empty.");

            if (_init != null)
            {
                value = _init(this, value);
            }

            _value = value;
            _previousJson = json;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

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

    public void Query(Action<T> action)
    {
        _asyncLock.Wait();
        try
        {
            action(_value);
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public TResult Query<TResult>(Func<T, TResult> selector)
    {
        _asyncLock.Wait();
        try
        {
            return selector(_value);
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public T Enter()
    {
        _asyncLock.Wait();
        return _value;
    }

    public async ValueTask<T> EnterAsync()
    {
        await _asyncLock.WaitAsync();
        return _value;
    }

    public void Exit()
    {
        try
        {
            Save(_value);
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public void Modify(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _asyncLock.Wait();

        try
        {
            action(_value);
            Save(_value);
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    private void Save(T value)
    {
        string newJson = JsonConvert.SerializeObject(value, Formatting.Indented);

        if (newJson == _previousJson)
        {
            return;
        }

        if (_jsonPath is not null)
        {
            string temporary = _jsonPath + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(newJson));
                stream.Flush(flushToDisk: true);
            }

            _replaceFile(temporary, _jsonPath);
        }

        _previousJson = newJson;
    }
}
