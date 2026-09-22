using MihuBot.Helpers;
using Newtonsoft.Json;

namespace MihuBot.Tests;

public sealed class SynchronizedLocalJsonStoreTests
{
    [Fact]
    public async Task LegacyEnterExitKeepsReferencesAndRoundTripsInitializedValues()
    {
        using var files = new Files();
        File.WriteAllText(files.Path, """{"Value":4}""");
        var store = new SynchronizedLocalJsonStore<Value>(files.Path, (_, value) =>
        {
            value.ValueText = "initialized";
            return value;
        });
        Value original = store.DangerousGetValue();
        Assert.Equal(4, original.Number);
        Assert.Equal("initialized", original.ValueText);
        Assert.Same(original, await store.EnterAsync());
        original.Number = 5;
        store.Exit();
        Assert.Same(original, store.DangerousGetValue());
        var reopened = new SynchronizedLocalJsonStore<Value>(files.Path);
        Assert.Equal(5, reopened.Query(value => value.Number));
        Assert.Equal("initialized", await reopened.QueryAsync(value => value.ValueText));
    }

    [Fact]
    public void MissingDirectoryStillAllowsInitializationBeforeTheFirstSave()
    {
        using var files = new Files();
        string directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(files.Path)!, "new");
        var store = new SynchronizedLocalJsonStore<Value>(System.IO.Path.Combine(directory, "store.json"));
        Assert.Equal(0, store.Query(v => v.Number));
        Directory.CreateDirectory(directory);
        store.Enter().Number = 2;
        store.Exit();
        Assert.Equal(2, new SynchronizedLocalJsonStore<Value>(System.IO.Path.Combine(directory, "store.json")).Query(v => v.Number));
    }

    [Fact]
    public async Task FailedLegacySaveReleasesLockAndRetriesTheDirtyValue()
    {
        using var files = new Files();
        bool fail = true;
        int writes = 0;
        var store = new SynchronizedLocalJsonStore<Value>(files.Path, null, (source, destination) =>
        {
            writes++;

            if (fail)
            {
                throw new IOException("Simulated failure");
            }

            File.Move(source, destination, overwrite: true);
        });
        Value value = store.Enter();
        value.Number = 7;
        Assert.Throws<IOException>(store.Exit);
        Assert.False(File.Exists(files.Path));
        Assert.Equal(7, await store.QueryAsync(v => v.Number).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        fail = false;
        Assert.Same(value, await store.EnterAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        store.Exit();
        Assert.Equal(2, writes);
        store.Enter();
        store.Exit();
        Assert.Equal(2, writes);
        Assert.Equal(7, new SynchronizedLocalJsonStore<Value>(files.Path).Query(v => v.Number));
    }

    [Fact]
    public async Task CallbackAndSerializationFailuresDoNotLeakTheLock()
    {
        var store = new SynchronizedLocalJsonStore<Value>(new Value { Number = 3 });
        Assert.Throws<InvalidOperationException>(() => store.Modify(_ => throw new InvalidOperationException()));
        Assert.Equal(3, await store.QueryAsync(v => v.Number).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        var broken = new SynchronizedLocalJsonStore<BrokenValue>(new BrokenValue());
        broken.Enter();
        Assert.Throws<JsonSerializationException>(broken.Exit);
        Assert.True(await broken.QueryAsync(_ => true).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ModifyKeepsReferencesAndSerializesConcurrentChanges()
    {
        using var files = new Files();
        var store = new SynchronizedLocalJsonStore<Value>(files.Path);
        Value original = store.DangerousGetValue();
        Parallel.For(0, 100, _ => store.Modify(v => v.Number++));
        Assert.Same(original, store.DangerousGetValue());
        Assert.Equal(100, original.Number);
        Assert.Equal(100, new SynchronizedLocalJsonStore<Value>(files.Path).Query(v => v.Number));
    }

    [Fact]
    public async Task FailedModifyKeepsChangesInPlaceAndAllowsSavingAgain()
    {
        using var files = new Files();
        File.WriteAllText(files.Path, """{"Value":10}""");
        string before = File.ReadAllText(files.Path);
        bool fail = true;
        var store = new SynchronizedLocalJsonStore<Value>(files.Path, null, (source, destination) =>
        {
            if (fail)
            {
                throw new IOException("Simulated failure");
            }

            File.Move(source, destination, overwrite: true);
        });
        Value original = store.DangerousGetValue();
        Assert.Throws<IOException>(() => store.Modify(v => v.Number++));
        Assert.Same(original, store.DangerousGetValue());
        Assert.Equal(11, await store.QueryAsync(v => v.Number).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(before, File.ReadAllText(files.Path));
        fail = false;
        store.Modify(_ => { });
        Assert.Same(original, store.DangerousGetValue());
        Assert.Equal(11, new SynchronizedLocalJsonStore<Value>(files.Path).Query(v => v.Number));
    }

    [Fact]
    public async Task ModifyKeepsChangesAfterCallbackFailuresAndReleasesTheLock()
    {
        var store = new SynchronizedLocalJsonStore<Value>(new Value { Number = 3 });
        Assert.Throws<InvalidOperationException>(() => store.Modify(v =>
        {
            v.Number++;
            throw new InvalidOperationException();
        }));
        Assert.Equal(4, await store.QueryAsync(v => v.Number).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ReloadReappliesInitializationAndSkipsUnchangedFiles()
    {
        using var files = new Files();
        File.WriteAllText(files.Path, """{"Value":1}""");
        int initializations = 0;
        var store = new SynchronizedLocalJsonStore<Value>(files.Path, (_, value) =>
        {
            initializations++;
            value.ValueText = "initialized";
            return value;
        });
        Value original = store.DangerousGetValue();
        store.Reload();
        Assert.Same(original, store.DangerousGetValue());
        Assert.Equal(1, initializations);

        File.WriteAllText(files.Path, """{"Value":2}""");
        store.Reload();
        Assert.Equal(2, store.Query(v => v.Number));
        Assert.Equal("initialized", store.Query(v => v.ValueText));
        Assert.Equal(2, initializations);
        store.Modify(v => v.Number = 3);
        Value modified = store.DangerousGetValue();
        store.Reload();
        Assert.Same(modified, store.DangerousGetValue());
        Assert.Equal(2, initializations);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("")]
    public async Task FailedReloadKeepsTheValueAndReleasesTheLock(string json)
    {
        using var files = new Files();
        File.WriteAllText(files.Path, """{"Value":1}""");
        var store = new SynchronizedLocalJsonStore<Value>(files.Path);
        Value original = store.DangerousGetValue();
        File.WriteAllText(files.Path, json);
        Assert.ThrowsAny<JsonException>(store.Reload);
        Assert.Same(original, store.DangerousGetValue());
        Assert.Equal(1, await store.QueryAsync(v => v.Number).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        File.WriteAllText(files.Path, """{"Value":2}""");
        store.Reload();
        Assert.Equal(2, store.Query(v => v.Number));
    }

    [Fact]
    public void ReloadRejectsEmptyFileCreatedAfterInitialization()
    {
        using var files = new Files();
        var store = new SynchronizedLocalJsonStore<Value>(files.Path);
        File.WriteAllText(files.Path, string.Empty);
        Assert.Throws<JsonSerializationException>(store.Reload);
        Assert.Equal(0, store.Query(v => v.Number));
    }

    public sealed class Value
    {
        [JsonProperty("Value")]
        public int Number { get; set; }
        public string? ValueText { get; set; }
    }

    public sealed class BrokenValue
    {
        public string Value => throw new InvalidOperationException("Cannot serialize this value.");
    }

    private sealed class Files : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("MihuBot-JsonStore-");
        public string Path => System.IO.Path.Combine(_directory.FullName, "store.json");
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
