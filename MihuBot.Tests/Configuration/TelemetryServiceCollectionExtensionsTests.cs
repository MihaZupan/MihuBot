using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MihuBot.Configuration;
using MihuBot.Discord;
using MihuBot.RuntimeUtils;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MihuBot.Tests.Configuration;

public sealed class TelemetryServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(string? endpoint) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Endpoint"] = endpoint
        }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEndpointDoesNotRegisterTelemetry(string? endpoint)
    {
        var services = new ServiceCollection();
        services.AddMihuBotTelemetry(Configuration(endpoint));

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<TracerProvider>());
        Assert.Null(provider.GetService<MeterProvider>());
        Assert.False(Configuration(endpoint).IsConfigured(OptionalFeatures.Telemetry));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://collector:4318")]
    [InlineData("http://collector:4318?query=value")]
    [InlineData("http://collector:4318#fragment")]
    [InlineData("http://user:password@collector:4318")]
    public void InvalidEndpointFailsRegistration(string endpoint)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddMihuBotTelemetry(Configuration(endpoint)));
        Assert.Contains("Telemetry:Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://collector:4318", "http://collector:4318/v1/")]
    [InlineData("http://collector:4318/", "http://collector:4318/v1/")]
    [InlineData("https://collector/otel/", "https://collector/otel/v1/")]
    public void ExportersUseHttpProtobufAndSignalPaths(string endpoint, string expectedPrefix)
    {
        IConfiguration configuration = Configuration(endpoint);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddMihuBotTelemetry(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>();

        foreach (string signal in new[] { "traces", "metrics" })
        {
            Assert.Equal(OtlpExportProtocol.HttpProtobuf, options.Get(signal).Protocol);
            Assert.Equal(new Uri(expectedPrefix + signal), options.Get(signal).Endpoint);
        }
    }

    [Fact]
    public void ExportsCustomSpansAndMetricsWithSharedResourceAndNoLogExporter()
    {
        IConfiguration configuration = Configuration("http://collector:4318");
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddMihuBotTelemetry(configuration);
        using var handler = new RecordingHandler();
        services.PostConfigureAll<OtlpExporterOptions>(options =>
            options.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false));

        using ServiceProvider provider = services.BuildServiceProvider();
        var traces = provider.GetRequiredService<TracerProvider>();
        var metrics = provider.GetRequiredService<MeterProvider>();
        using var meter = new Meter("MihuBot.Tests.Telemetry");
        var histogram = meter.CreateHistogram<double>("mihubot.test.duration", "s");

        using (var activity = MihuBotAIActivitySource.Instance.StartActivity("telemetry-export-test"))
        {
            Assert.NotNull(activity);
            histogram.Record(0.25, new KeyValuePair<string, object?>("http.route", "/test/{id}"));
        }

        using var librarySource = new ActivitySource("MihuBot.Tests.Library");

        using (var activity = librarySource.StartActivity("library-export-test"))
        {
            Assert.NotNull(activity);
        }

        using (var activity = MihuBotDiscordActivitySource.Instance.StartActivity("discord-export-test"))
        {
            Assert.NotNull(activity);
        }

        Assert.True(traces.ForceFlush());
        Assert.True(metrics.ForceFlush());

        var traceExport = Assert.Single(handler.Exports, e =>
            e.Path == "/v1/traces" && e.Body.Contains("telemetry-export-test", StringComparison.Ordinal));
        Assert.Contains("library-export-test", traceExport.Body, StringComparison.Ordinal);
        Assert.Contains("MihuBot.Ai", traceExport.Body, StringComparison.Ordinal);
        Assert.Contains("discord-export-test", traceExport.Body, StringComparison.Ordinal);
        Assert.Contains("MihuBot.Discord", traceExport.Body, StringComparison.Ordinal);

        var metricExport = Assert.Single(handler.Exports, e =>
            e.Path == "/v1/metrics" && e.Body.Contains("mihubot.test.duration", StringComparison.Ordinal));
        Assert.Contains("http.route", metricExport.Body, StringComparison.Ordinal);
        Assert.Contains("/test/{id}", metricExport.Body, StringComparison.Ordinal);

        var traceResource = traces.GetResource().Attributes.ToDictionary();
        var metricResource = metrics.GetResource().Attributes.ToDictionary();
        Assert.Equal("mihubot", traceResource["service.name"]);
        Assert.Equal("mihubot", traceResource["service.namespace"]);
        Assert.Equal(traceResource["service.instance.id"], metricResource["service.instance.id"]);
        Assert.Equal("main", traceResource["service.instance.id"]);
        Assert.Contains("service.version", traceResource.Keys);

        Assert.All(handler.Exports, export => Assert.Equal("application/x-protobuf", export.ContentType));
        Assert.DoesNotContain(provider.GetServices<ILoggerProvider>(),
            logger => logger.GetType().FullName == "OpenTelemetry.Logs.OpenTelemetryLoggerProvider");
        Assert.DoesNotContain(handler.Exports, export => export.Path == "/v1/logs");
    }

    [Fact]
    public async Task ExportsIncomingAndOutgoingHttpTracesAndRuntimeMetrics()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Endpoint"] = "http://collector:4318"
        });
        builder.Logging.ClearProviders();
        builder.Services.AddMihuBotTelemetry(builder.Configuration);
        using var handler = new RecordingHandler();
        builder.Services.PostConfigureAll<OtlpExporterOptions>(options =>
            options.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false));

        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/telemetry-test/{id}", () => "ok");
        await app.StartAsync();

        try
        {
            using var client = new HttpClient();
            Assert.Equal("ok", await client.GetStringAsync($"{Assert.Single(app.Urls)}/telemetry-test/123"));

            Assert.True(app.Services.GetRequiredService<TracerProvider>().ForceFlush());
            Assert.True(app.Services.GetRequiredService<MeterProvider>().ForceFlush());

            string traces = string.Concat(handler.Exports.Where(e => e.Path == "/v1/traces").Select(e => e.Body));
            Assert.Contains("Microsoft.AspNetCore", traces, StringComparison.Ordinal);
            Assert.Contains("System.Net.Http", traces, StringComparison.Ordinal);
            Assert.Contains("/telemetry-test/{id}", traces, StringComparison.Ordinal);

            string metrics = string.Concat(handler.Exports.Where(e => e.Path == "/v1/metrics").Select(e => e.Body));
            Assert.Contains("System.Runtime", metrics, StringComparison.Ordinal);
            Assert.Contains("http.server.request.duration", metrics, StringComparison.Ordinal);
            Assert.Contains("http.client.request.duration", metrics, StringComparison.Ordinal);
            Assert.Contains("http.response.status_code", metrics, StringComparison.Ordinal);
            Assert.Contains("/telemetry-test/{id}", metrics, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path, string? ContentType, string Body)> Exports { get; } = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            Exports.Enqueue((request.RequestUri!.AbsolutePath, request.Content.Headers.ContentType?.MediaType, Encoding.UTF8.GetString(body)));

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
