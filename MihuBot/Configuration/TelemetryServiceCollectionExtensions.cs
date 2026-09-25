using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MihuBot.Configuration;

public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddMihuBotTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.IsConfigured(OptionalFeatures.Telemetry))
        {
            return services;
        }

        string endpointValue = configuration["Telemetry:Endpoint"];

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException(
                "Telemetry:Endpoint must be an absolute HTTP(S) base URL without credentials, a query, or a fragment.");
        }

        string baseUrl = endpoint.AbsoluteUri.TrimEnd('/');

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: "mihubot",
                serviceNamespace: "mihubot",
                serviceVersion: BuildInfo.GetCommitId(),
                serviceInstanceId: "main"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("*")
                .AddOtlpExporter("traces", options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = new Uri($"{baseUrl}/v1/traces");
                }))
            .WithMetrics(metrics => metrics
                .AddMeter("*")
                .SetExemplarFilter(ExemplarFilterType.TraceBased)
                .AddOtlpExporter("metrics", options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Endpoint = new Uri($"{baseUrl}/v1/metrics");
                }));

        return services;
    }
}
