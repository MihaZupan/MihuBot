using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.API;
using MihuBot.Configuration;
using MihuBot.RuntimeUtils.AI;
using Octokit;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelsApiTests
{
    [Theory]
    [InlineData("""{"repository":"dotnet/runtime","number":123}""", HttpStatusCode.OK)]
    [InlineData("""{"repository":"dotnet/aspnetcore","number":456}""", HttpStatusCode.OK)]
    [InlineData("""{"repository":"dotnet/runtime","number":0}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime","number":-1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime","number":2147483648}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"number":1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"https://github.com/dotnet/runtime","number":1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/../private","number":1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/..","number":1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime?x","number":1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime","number":1,"body":"Caller content"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime","number":1,"labelPrefix":null}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime","number":1,"labelPrefix":""}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"repository":"dotnet/runtime","number":1,"labelPrefix":" "}""", HttpStatusCode.BadRequest)]
    public async Task EndpointValidatesRequestAndReturnsCamelCaseJson(string json, HttpStatusCode expectedStatus)
    {
        var cache = new PredictionCache();
        await using var app = CreateApp(cache);
        await app.StartAsync();
        using var http = CreateClient(app);
        using var response = await http.PostAsync("/api/RuntimeUtils/AreaLabels/Predict", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedStatus == HttpStatusCode.OK ? 1 : 0, cache.Calls);
        if (expectedStatus == HttpStatusCode.OK)
        {
            Assert.Equal("area-", cache.LabelPrefix);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Array, result.RootElement.ValueKind);
            Assert.Equal("area-Test", result.RootElement[0].GetProperty("labelName").GetString());
            Assert.Equal(0.9, result.RootElement[0].GetProperty("confidence").GetDouble());
        }
    }

    [Theory]
    [InlineData("null", HttpStatusCode.OK)]
    [InlineData("""{"labelName":"area-Other","confidence":0}""", HttpStatusCode.OK)]
    [InlineData("""{"labelName":"area-Other","confidence":0.85}""", HttpStatusCode.OK)]
    [InlineData("""{"labelName":"area-Other","confidence":1}""", HttpStatusCode.OK)]
    [InlineData("""{}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"confidence":0.85}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":"","confidence":0.85}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":" ","confidence":0.85}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":"area-Other\ninjected log","confidence":0.85}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":"area-Other"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":"area-Other","confidence":null}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":"area-Other","confidence":-0.1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":"area-Other","confidence":1.1}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"labelName":"area-Other","confidence":0.85,"body":"Caller content"}""", HttpStatusCode.BadRequest)]
    public async Task ExistingPredictionIsOptionalAndValidated(string existingPrediction, HttpStatusCode expectedStatus)
    {
        var cache = new PredictionCache();
        await using var app = CreateApp(cache);
        await app.StartAsync();
        using var http = CreateClient(app);
        string json = $$"""{"repository":"dotnet/runtime","number":123,"existingPrediction":{{existingPrediction}}}""";
        using var response = await http.PostAsync("/api/RuntimeUtils/AreaLabels/Predict", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedStatus == HttpStatusCode.OK ? 1 : 0, cache.Calls);
    }

    [Fact]
    public async Task ExistingPredictionsAreLoggedPerRequestWithoutChangingCachedResults()
    {
        var cache = new PredictionCache();
        var logs = new ComparisonLoggerProvider();
        await using var app = CreateApp(cache, logs);
        await app.StartAsync();
        using var http = CreateClient(app);

        foreach (string label in new[] { "area-First", "area-Second" })
        {
            using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict",
                new { repository = "dotnet/runtime", number = 123, existingPrediction = new { labelName = label, confidence = 0.85 } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var suggestions = await response.Content.ReadFromJsonAsync<AreaLabelSuggestion[]>();
            Assert.Equal(new AreaLabelSuggestion("area-Test", 0.9), Assert.Single(suggestions!));
        }

        Assert.Equal(2, logs.Messages.Count);
        Assert.Contains("ML area-First", logs.Messages[0]);
        Assert.Contains("ML area-Second", logs.Messages[1]);
        Assert.All(logs.Messages, message =>
        {
            Assert.Contains("<https://github.com/dotnet/runtime/issues/123>", message);
            Assert.Contains("85", message);
            Assert.Contains("LLM area-Test", message);
            Assert.Contains("90", message);
            Assert.Matches(@" in \d+[.,]\d{2}s:", message);
            Assert.DoesNotContain('\n', message);
        });
        Assert.Equal(["AreaLabels:dotnet/runtime:123:area-", "AreaLabels:dotnet/runtime:123:area-"], cache.Keys);
    }

    [Fact]
    public async Task MissingDiscussionReturnsNotFound()
    {
        await using var app = CreateApp(new PredictionCache
        {
            Error = new NotFoundException("Not found.", HttpStatusCode.NotFound)
        });
        await app.StartAsync();
        using var http = CreateClient(app);
        using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict",
            new { repository = "dotnet/runtime", number = 123 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("component:")]
    [InlineData("type/")]
    [InlineData("OS-")]
    public async Task CustomPrefixIsPassedToDetector(string labelPrefix)
    {
        var cache = new PredictionCache();
        await using var app = CreateApp(cache);
        await app.StartAsync();
        using var http = CreateClient(app);
        using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict",
            new { repository = "dotnet/runtime", number = 123, labelPrefix });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(labelPrefix.ToLowerInvariant(), cache.LabelPrefix);
        var suggestions = await response.Content.ReadFromJsonAsync<AreaLabelSuggestion[]>();
        Assert.Equal($"{labelPrefix.ToLowerInvariant()}Test", Assert.Single(suggestions!).LabelName);
    }

    [Fact]
    public async Task OversizedPrefixIsRejected()
    {
        var cache = new PredictionCache();
        await using var app = CreateApp(cache);
        await app.StartAsync();
        using var http = CreateClient(app);
        using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict",
            new { repository = "dotnet/runtime", number = 123, labelPrefix = new string('a', 101) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, cache.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable)]
    public async Task UpstreamFailuresAreNotSuccessfulEmptyPredictions(HttpStatusCode upstream, HttpStatusCode expected)
    {
        await using var app = CreateApp(new PredictionCache
        {
            Error = upstream == HttpStatusCode.NotFound ? new NotFoundException("Not found", upstream) : new ApiException("GitHub error", upstream)
        });
        await app.StartAsync();
        using var http = CreateClient(app);
        using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict", new { repository = "dotnet/runtime", number = 1 });
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task EndpointIsAbsentWhenDependenciesAreNotConfigured()
    {
        await using var app = CreateApp(null);
        await app.StartAsync();
        using var http = CreateClient(app);
        using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict", new { repository = "dotnet/runtime", number = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TimeoutReturnsGatewayTimeout()
    {
        await using var app = CreateApp(new PredictionCache { Error = new TaskCanceledException() });
        await app.StartAsync();
        using var http = CreateClient(app);
        using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict", new { repository = "dotnet/runtime", number = 1 });
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentPredictionsAreBounded()
    {
        var release = new TaskCompletionSource();
        var cache = new PredictionCache { Release = release.Task };
        await using var app = CreateApp(cache);
        await app.StartAsync();
        using var http = CreateClient(app);
        Task<HttpResponseMessage>[] requests = Enumerable.Range(1, 10)
            .Select(number => http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict", new { repository = "dotnet/runtime", number }))
            .ToArray();
        try
        {
            await cache.TenRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsJsonAsync("/api/RuntimeUtils/AreaLabels/Predict", new { repository = "dotnet/runtime", number = 11 });
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        finally
        {
            release.SetResult();
            foreach (var response in await Task.WhenAll(requests))
            {
                response.Dispose();
            }
        }
    }

    private static WebApplication CreateApp(PredictionCache? cache, ILoggerProvider? loggerProvider = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
            builder.Logging.AddFilter<ComparisonLoggerProvider>(typeof(AreaLabelsController).FullName, LogLevel.Information);
        }
        builder.Services.AddControllers().AddApplicationPart(typeof(AreaLabelsController).Assembly);
        builder.Services.AddRemoveUnavailableControllersConvention();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddConcurrencyLimiter("area-labels", limiter => limiter.PermitLimit = 10);
        });
        if (cache is not null)
        {
            builder.Services.AddSingleton(new AreaLabelDetector(null!, null!, null!, null!, cache, null!));
        }
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapControllers();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    private sealed class PredictionCache : HybridCache
    {
        private int _calls;
        public int Calls => _calls;
        public System.Collections.Concurrent.ConcurrentQueue<string> Keys { get; } = new();
        public string? LabelPrefix { get; private set; }
        public Exception? Error { get; init; }
        public Task? Release { get; init; }
        public TaskCompletionSource TenRequestsStarted { get; } = new();

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
        {
            Keys.Enqueue(key);
            LabelPrefix = key.Split(':', 4)[3];
            if (Interlocked.Increment(ref _calls) == 10)
            {
                TenRequestsStarted.TrySetResult();
            }
            if (Error is not null)
            {
                throw Error;
            }
            if (Release is not null)
            {
                await Release.WaitAsync(cancellationToken);
            }
            AreaLabelSuggestion[] suggestions = [new($"{LabelPrefix}Test", 0.9)];
            return suggestions is T result ? result : throw new InvalidOperationException("Unexpected cache value type.");
        }

        public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ComparisonLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) =>
            new ComparisonLogger(categoryName == typeof(AreaLabelsController).FullName ? Messages : null);

        public void Dispose() { }

        private sealed class ComparisonLogger(List<string>? messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    messages?.Add(formatter(state, exception));
                }
            }
        }
    }
}
