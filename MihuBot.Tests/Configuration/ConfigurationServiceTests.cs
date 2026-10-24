using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.Helpers;

namespace MihuBot.Tests.Configuration;

public sealed class ConfigurationServiceTests
{
    [Fact]
    public void LoggingServiceGraphHasNoCircularDependencies()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<ServiceConfiguration>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<LoggerOptions>(_ => throw new InvalidOperationException("Validation must not instantiate the logger."));
        services.AddSingleton<Logger>();
        services.AddSingleton<ILoggerProvider, LoggerAdapterLoggerProvider>();
        DatabaseSetupHelper.AddPooledDbContextFactory<LogsDbContext>(services, ":memory:");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadsEditsAndAtomicReplacements(bool global)
    {
        using var files = new Files();
        files.Write(global, """{"Key":"old","Removed":"value"}""");
        using var service = files.CreateService();
        ulong? context = global ? null : 42;
        Assert.True(service.TryGet(context, "KEY", out string initial));
        Assert.Equal("old", initial);

        files.Write(global, """{"key":"edited","Added":"retained"}""");
        Assert.True(service.TryGet(context, "KEY", out string edited));
        Assert.Equal("edited", edited);
        Assert.False(service.TryGet(context, "Removed", out _));

        files.Write(global, """{"KEY":"replaced","Added":"retained"}""", replace: true);
        Assert.True(service.TryGet(context, "key", out string replaced));
        Assert.Equal("replaced", replaced);
        service.Set(context, "Local", "written");
        Assert.True(service.Remove(context, "kEy"));

        using var reopened = files.CreateService();
        Assert.True(reopened.TryGet(context, "added", out string retained));
        Assert.Equal("retained", retained);
        Assert.True(reopened.TryGet(context, "local", out string written));
        Assert.Equal("written", written);
        Assert.False(reopened.TryGet(context, "key", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WatchesFilesThatDoNotExistAtStartup(bool global)
    {
        using var files = new Files();
        using var service = files.CreateService();
        ulong? context = global ? null : 42;
        Assert.False(service.TryGet(context, "Key", out _));
        files.Write(global, """{"Key":"created"}""");
        Assert.True(service.TryGet(context, "key", out string value));
        Assert.Equal("created", value);
    }

    [Theory]
    [InlineData(false, "{")]
    [InlineData(true, "{")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, """{"42":null}""")]
    [InlineData(true, """{"Key":"a","KEY":"b"}""")]
    public void InvalidEditsAreLoggedAndKeepPreviousValuesUntilCorrected(bool global, string invalidJson)
    {
        using var files = new Files();
        files.Write(global, """{"Key":"old"}""");
        var errors = new ConcurrentQueue<string>();
        using var service = files.CreateService(errors.Enqueue);
        ulong? context = global ? null : 42;

        File.WriteAllText(files.Path(global), invalidJson);
        files.Notify(global);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, message => message.Contains(
            $"Could not reload {System.IO.Path.GetFileName(files.Path(global))}; keeping the previous configuration. Exception:",
            StringComparison.Ordinal));
        Assert.True(service.TryGet(context, "Key", out string previous));
        Assert.Equal("old", previous);

        files.Write(global, """{"Key":"corrected"}""");
        Assert.True(service.TryGet(context, "KEY", out string value));
        Assert.Equal("corrected", value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedFilesKeepPreviousValuesAndCanBeRecreated(bool global)
    {
        using var files = new Files();
        files.Write(global, """{"Key":"old"}""");
        var errors = new ConcurrentQueue<string>();
        using var service = files.CreateService(errors.Enqueue);
        ulong? context = global ? null : 42;

        File.Delete(files.Path(global));
        files.Notify(global);
        Assert.NotEmpty(errors);
        Assert.True(service.TryGet(context, "Key", out string previous));
        Assert.Equal("old", previous);

        files.Write(global, """{"Key":"recreated"}""");
        Assert.True(service.TryGet(context, "Key", out string value));
        Assert.Equal("recreated", value);
    }

    [Fact]
    public void DisposedWatchersDoNotReload()
    {
        using var files = new Files();
        files.Write(global: true, """{"Key":"old"}""");
        files.Write(global: false, """{"Key":"old"}""");
        using var service = files.CreateService();
        var inFlight = files.Subscriptions.ToArray();
        service.Dispose();

        files.Write(global: true, """{"Key":"new"}""");
        files.Write(global: false, """{"Key":"new"}""");

        foreach (var subscription in inFlight)
        {
            subscription.Reload();
        }

        Assert.Empty(files.Subscriptions);
        Assert.True(service.TryGet(null, "Key", out string global));
        Assert.Equal("old", global);
        Assert.True(service.TryGet(42, "Key", out string contextual));
        Assert.Equal("old", contextual);
    }

    [Fact]
    public void SetAndRemoveRemainCaseInsensitiveAndPersistContextRemoval()
    {
        using var files = new Files();
        using var service = files.CreateService();
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
        const int Count = 8;

        using var files = new Files();
        using var service = files.CreateService();
        ulong? context = global ? null : 42;
        service.Set(context, "Initial", "old");
        files.Write(global, """{"Initial":"edited"}""");
        Assert.True(service.TryGet(context, "Initial", out string edited));
        Assert.Equal("edited", edited);

        await Parallel.ForAsync(0, Count, (index, _) =>
        {
            string key = $"Key{index}";
            service.Set(context, key, "value");
            files.Notify(global);
            Assert.True(service.TryGet(context, key, out string value));
            Assert.Equal("value", value);
            return ValueTask.CompletedTask;
        });

        using var reopened = files.CreateService();
        Assert.True(reopened.TryGet(context, "Initial", out string initial));
        Assert.Equal("edited", initial);

        for (int i = 0; i < Count; i++)
        {
            Assert.True(reopened.TryGet(context, $"Key{i}", out string value));
            Assert.Equal("value", value);
        }
    }

    [Fact]
    public void TypedIntegerConfigurationDefaultsOnlyWhenMissing()
    {
        using var files = new Files();
        using var store = files.CreateService();
        IConfigurationService configuration = store;

        Assert.Equal(8, configuration.GetInt32(null, "Count", 8));
        configuration.Set(null, "Count", " -4 ");
        Assert.Equal(-4, configuration.GetInt32(null, "Count", 8));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("2.5")]
    [InlineData("2147483648")]
    public void TypedIntegerConfigurationRejectsMalformedValues(string value)
    {
        using var files = new Files();
        using var store = files.CreateService();
        IConfigurationService configuration = store;
        configuration.Set(null, "Count", value);

        Assert.Throws<InvalidOperationException>(() => configuration.GetInt32(null, "Count", 8));
    }

    private sealed class Files : IDisposable
    {
        private readonly DirectoryInfo _directory = System.IO.Directory.CreateTempSubdirectory("MihuBot-Configuration-");
        public List<Subscription> Subscriptions { get; } = [];
        public string Directory => _directory.FullName;
        public string Path(bool global) => System.IO.Path.Combine(Directory, global ? "GlobalConfiguration.json" : "Configuration.json");

        public ConfigurationService CreateService(Action<string>? logError = null) =>
            new(Directory, logError, (name, reload) =>
            {
                var subscription = new Subscription(this, name, reload);
                Subscriptions.Add(subscription);
                return subscription;
            });

        public void Notify(bool global)
        {
            foreach (var subscription in Subscriptions.Where(s => s.Name == System.IO.Path.GetFileName(Path(global))).ToArray())
            {
                subscription.Reload();
            }
        }

        public void Write(bool global, string json, bool replace = false)
        {
            string path = Path(global);
            string contents = global ? json : $$"""{"42":{{json}}}""";
            File.WriteAllText(replace ? path + ".new" : path, contents);

            if (replace)
            {
                File.Move(path + ".new", path, overwrite: true);
            }

            Notify(global);
        }

        public void Dispose() => _directory.Delete(recursive: true);

        public sealed class Subscription(Files files, string name, Action reload) : IDisposable
        {
            public string Name => name;
            public void Reload() => reload();
            public void Dispose() => files.Subscriptions.Remove(this);
        }
    }
}
