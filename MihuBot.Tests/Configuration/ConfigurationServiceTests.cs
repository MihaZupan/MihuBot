using Microsoft.Extensions.Logging;
using MihuBot.Configuration;

namespace MihuBot.Tests.Configuration;

public sealed class ConfigurationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadsEditsAndAtomicReplacements(bool global)
    {
        using var files = new Files();
        files.Write(global, """{"Key":"old","Removed":"value"}""");
        using var service = new ConfigurationService(files.Directory, new TestLogger());
        ulong? context = global ? null : 42;
        Assert.True(service.TryGet(context, "KEY", out string initial));
        Assert.Equal("old", initial);

        files.Write(global, """{"key":"edited","Added":"retained"}""");
        await WaitFor(() => service.TryGet(context, "KEY", out string value) && value == "edited");
        Assert.False(service.TryGet(context, "Removed", out _));

        files.Write(global, """{"KEY":"replaced","Added":"retained"}""", replace: true);
        await WaitFor(() => service.TryGet(context, "key", out string value) && value == "replaced");
        service.Set(context, "Local", "written");
        Assert.True(service.Remove(context, "kEy"));

        using var reopened = new ConfigurationService(files.Directory, new TestLogger());
        Assert.True(reopened.TryGet(context, "added", out string retained));
        Assert.Equal("retained", retained);
        Assert.True(reopened.TryGet(context, "local", out string written));
        Assert.Equal("written", written);
        Assert.False(reopened.TryGet(context, "key", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchesFilesThatDoNotExistAtStartup(bool global)
    {
        using var files = new Files();
        using var service = new ConfigurationService(files.Directory, new TestLogger());
        ulong? context = global ? null : 42;
        Assert.False(service.TryGet(context, "Key", out _));
        files.Write(global, """{"Key":"created"}""");
        await WaitFor(() => service.TryGet(context, "key", out string value) && value == "created");
    }

    [Theory]
    [InlineData(false, "{")]
    [InlineData(true, "{")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, """{"42":null}""")]
    [InlineData(true, """{"Key":"a","KEY":"b"}""")]
    public async Task InvalidEditsAreLoggedAndKeepPreviousValuesUntilCorrected(bool global, string invalidJson)
    {
        using var files = new Files();
        files.Write(global, """{"Key":"old"}""");
        var logger = new TestLogger();
        using var service = new ConfigurationService(files.Directory, logger);
        ulong? context = global ? null : 42;

        File.WriteAllText(files.Path(global), invalidJson);
        await WaitFor(() => logger.ErrorCount > 0);
        Assert.True(service.TryGet(context, "Key", out string previous));
        Assert.Equal("old", previous);

        files.Write(global, """{"Key":"corrected"}""");
        await WaitFor(() => service.TryGet(context, "KEY", out string value) && value == "corrected");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedFilesKeepPreviousValuesAndCanBeRecreated(bool global)
    {
        using var files = new Files();
        files.Write(global, """{"Key":"old"}""");
        var logger = new TestLogger();
        using var service = new ConfigurationService(files.Directory, logger);
        ulong? context = global ? null : 42;

        File.Delete(files.Path(global));
        await WaitFor(() => logger.ErrorCount > 0);
        Assert.True(service.TryGet(context, "Key", out string previous));
        Assert.Equal("old", previous);

        files.Write(global, """{"Key":"recreated"}""");
        await WaitFor(() => service.TryGet(context, "Key", out string value) && value == "recreated");
    }

    [Fact]
    public async Task DisposedWatchersDoNotReload()
    {
        using var files = new Files();
        files.Write(global: true, """{"Key":"old"}""");
        files.Write(global: false, """{"Key":"old"}""");
        using var service = new ConfigurationService(files.Directory, new TestLogger());
        service.Dispose();

        files.Write(global: true, """{"Key":"new"}""");
        files.Write(global: false, """{"Key":"new"}""");
        await Task.Delay(1000);
        Assert.True(service.TryGet(null, "Key", out string global));
        Assert.Equal("old", global);
        Assert.True(service.TryGet(42, "Key", out string contextual));
        Assert.Equal("old", contextual);
    }

    [Fact]
    public void SetAndRemoveRemainCaseInsensitiveAndPersistContextRemoval()
    {
        using var files = new Files();
        using var service = new ConfigurationService(files.Directory, new TestLogger());
        service.Set(42, "Key", "first");
        service.Set(42, "KEY", "second");
        service.Set(null, "Key", "global");
        Assert.True(service.TryGet(42, "key", out string value));
        Assert.Equal("second", value);
        Assert.False(service.Remove(43, "key"));
        Assert.False(service.Remove(42, "missing"));
        Assert.True(service.Remove(42, "kEy"));
        Assert.True(service.Remove(null, "KEY"));
        Assert.False(service.Remove(null, "Key"));
        Assert.Equal("{}", File.ReadAllText(files.Path(global: false)));
        Assert.Equal("{}", File.ReadAllText(files.Path(global: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentWritesAndReloadsPreserveAllValues(bool global)
    {
        using var files = new Files();
        using var service = new ConfigurationService(files.Directory, new TestLogger());
        ulong? context = global ? null : 42;
        service.Set(context, "Initial", "old");
        files.Write(global, """{"Initial":"edited"}""");
        await WaitFor(() => service.TryGet(context, "Initial", out string value) && value == "edited");

        await Parallel.ForAsync(0, 100, (index, _) =>
        {
            string key = $"Key{index}";
            service.Set(context, key, "value");
            Assert.True(service.TryGet(context, key, out string value));
            Assert.Equal("value", value);
            return ValueTask.CompletedTask;
        });

        using var reopened = new ConfigurationService(files.Directory, new TestLogger());
        Assert.True(reopened.TryGet(context, "Initial", out string initial));
        Assert.Equal("edited", initial);

        for (int i = 0; i < 100; i++)
        {
            Assert.True(reopened.TryGet(context, $"Key{i}", out string value));
            Assert.Equal("value", value);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class TestLogger : ILogger<ConfigurationService>
    {
        private int _errorCount;
        public int ErrorCount => Volatile.Read(ref _errorCount);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Interlocked.Increment(ref _errorCount);
            }
        }
    }

    private sealed class Files : IDisposable
    {
        private readonly DirectoryInfo _directory = System.IO.Directory.CreateTempSubdirectory("MihuBot-Configuration-");
        public string Directory => _directory.FullName;
        public string Path(bool global) => System.IO.Path.Combine(Directory, global ? "GlobalConfiguration.json" : "Configuration.json");

        public void Write(bool global, string json, bool replace = false)
        {
            string path = Path(global);
            string contents = global ? json : $$"""{"42":{{json}}}""";
            File.WriteAllText(replace ? path + ".new" : path, contents);

            if (replace)
            {
                File.Move(path + ".new", path, overwrite: true);
            }
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
