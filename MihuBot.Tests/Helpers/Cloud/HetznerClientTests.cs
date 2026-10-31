using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using MihuBot.DB;
using MihuBot.Helpers.Cloud;

namespace MihuBot.Tests.Helpers.Cloud;

public sealed class HetznerClientTests
{
    [Theory]
    [InlineData(false, "POST")]
    [InlineData(true, "DELETE")]
    public async Task FailedRequestsIncludeHetznerErrorAndHttpStatus(bool delete, string method)
    {
        using var handler = new ErrorHandler("""{"error":{"code":"invalid_input","message":"server type is not available","details":{"fields":[{"name":"user_data","errors":["private startup data"]}]}}}""");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => delete
            ? client.DeleteServerAsync(123, CancellationToken.None)
            : client.CreateServerAsync("runner-test", "ubuntu-24.04", "hel1", "cpx41", "private startup data", CancellationToken.None));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains($"Hetzner {method} https://api.hetzner.cloud/v1/servers", exception.Message);
        Assert.Contains("invalid_input: server type is not available", exception.Message);
        Assert.DoesNotContain("private startup data", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"error":"unexpected shape"}""")]
    public async Task InvalidErrorBodiesPreserveHttpFailure(string body)
    {
        using var handler = new ErrorHandler(body);
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteServerAsync(123, CancellationToken.None));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains("DELETE https://api.hetzner.cloud/v1/servers/123", exception.Message);
        Assert.Contains("UnprocessableEntity: unknown: No error message", exception.Message);
    }

    private static HetznerClient CreateClient(HttpClient http)
    {
        var logger = (Logger)RuntimeHelpers.GetUninitializedObject(typeof(Logger));
        typeof(Logger).GetField("LogChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(logger, Channel.CreateUnbounded<LogDbEntry>());

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hetzner:ApiKey"] = "test-key",
        }).Build();

        return new HetznerClient(http, logger, configuration);
    }

    private sealed class ErrorHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(body) });
    }
}
