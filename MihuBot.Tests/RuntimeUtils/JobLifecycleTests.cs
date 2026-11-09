using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.Helpers;
using MihuBot.RuntimeUtils;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class JobLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupFailureCompletesTheJobAndPersistsItsFailedStatus(bool missingConfiguration)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new DbFactory(new DbContextOptionsBuilder<MihuBotDbContext>().UseSqlite(connection).Options);

        await using (var context = factory.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var logger = (Logger)RuntimeHelpers.GetUninitializedObject(typeof(Logger));
        typeof(Logger).GetField("LogChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(logger, Channel.CreateUnbounded<LogDbEntry>());

        using var http = new HttpClient();
        var configuration = new ConfigurationBuilder().Build();
        var configurationService = new EmptyConfigurationService();

        // Avoid starting the service's file-backed state and background persistence.
        var parent = (RuntimeUtilsService)RuntimeHelpers.GetUninitializedObject(typeof(RuntimeUtilsService));
        SetField(parent, nameof(RuntimeUtilsService.Logger), logger);
        SetField(parent, nameof(RuntimeUtilsService.Configuration), configuration);
        SetField(parent, nameof(RuntimeUtilsService.ConfigurationService), configurationService);
        SetField(parent, "_mihuBotDb", factory);
        SetField(parent, "_jobs", new Dictionary<string, JobBase>(StringComparer.Ordinal));
        SetField(parent, "_runnerPersistentStorage", new Lazy<StorageClient>(() => new StorageClient(http, "runner-persistent", "test-key", isPublic: false)));

        var job = new StartupFailureJob(parent, missingConfiguration);
        Assert.False(job.Completed);

        await job.RunJobAsync();

        Assert.True(job.Completed);
        Assert.True(job.Completion.IsCompletedSuccessfully);
        Assert.Equal(!missingConfiguration, job.Initialized);
        Assert.False(job.RunnerStarted);
        Assert.False(job.WasCancelled);
        Assert.Null(job.TrackingIssue);
        Assert.True(job.LogArtifactSaveRequested);

        string expectedError = missingConfiguration
            ? $"Missing configuration for '{OptionalFeatures.AzureStorageRuntimeUtils.Description}' ({string.Join(", ", OptionalFeatures.AzureStorageRuntimeUtils.Keys)}). Aborting ..."
            : "Initialization failed";
        Assert.Equal(expectedError, job.ErrorMessage);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var streamedLogs = new List<string>();

        await foreach (string? line in job.StreamLogsAsync(timeout.Token))
        {
            if (line is not null)
            {
                streamedLogs.Add(line);
            }
        }

        Assert.Equal(job.GetLogSnapshot(null), streamedLogs);
        Assert.Contains(streamedLogs, line => line.Contains(expectedError, StringComparison.Ordinal));

        CompletedJobRecord record = await parent.TryGetCompletedJobRecordAsync(job.ExternalId, timeout.Token);
        Assert.NotNull(record);
        Assert.Equal(expectedError, record.ErrorMessage);
        Assert.Equal(job.Stopwatch.Elapsed, record.Duration);
        Assert.Equal(job.JobTitle, record.Title);

        RuntimeUtilsJobStatusResponse status = await parent.TryGetJobStatusAsync(job.ExternalId, timeout.Token);
        Assert.Equal("failed", status.State);
        Assert.Equal(expectedError, status.Error);
    }

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class StartupFailureJob : JobBase
    {
        private readonly bool _missingConfiguration;

        public StartupFailureJob(RuntimeUtilsService parent, bool missingConfiguration) : base(parent, "test-user", "")
        {
            _missingConfiguration = missingConfiguration;
            SuppressTrackingIssue = true;
        }

        public override string JobTitlePrefix => "Test";
        protected override OptionalFeature[] RequiredFeatures => _missingConfiguration ? [OptionalFeatures.AzureStorageRuntimeUtils] : [];
        public Task Completion => JobCompletionTcs.Task;
        public bool Initialized { get; private set; }
        public bool RunnerStarted { get; private set; }
        public bool LogArtifactSaveRequested { get; private set; }

        protected override Task SaveLogsArtifactAsync()
        {
            LogArtifactSaveRequested = true;
            return Task.CompletedTask;
        }

        protected override Task InitializeAsync(CancellationToken jobTimeout)
        {
            Initialized = true;
            throw new InvalidOperationException("Initialization failed");
        }

        protected override Task RunJobAsyncCore(CancellationToken jobTimeout)
        {
            RunnerStarted = true;
            return Task.CompletedTask;
        }
    }

    private sealed class DbFactory(DbContextOptions<MihuBotDbContext> options) : IDbContextFactory<MihuBotDbContext>
    {
        public MihuBotDbContext CreateDbContext() => new(options);
    }

    private sealed class EmptyConfigurationService : IConfigurationService
    {
        public void Set(ulong? context, string key, string value) => throw new NotSupportedException();
        public bool Remove(ulong? context, string key) => throw new NotSupportedException();

        public bool TryGet(ulong? context, string key, out string value)
        {
            value = null!;
            return false;
        }
    }
}
