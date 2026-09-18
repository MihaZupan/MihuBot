using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.Helpers.AI;
using MihuBot.RuntimeUtils.AI;
using MihuBot.Tests.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelsMcpTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("component:")]
    public async Task ToolIsDiscoverableAndReturnsCachedPredictions(string? labelPrefix)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        var detector = new AreaLabelDetector(null!, null!, null!, null!, cache, null!, new TestConfigurationService());
        List<string> logs = [];
        var server = new McpServer(logs.Add, null!, detector);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<McpServer>(server);
        await using var app = builder.Build();
        app.MapMcp("/mcp");

        string prefix = labelPrefix ?? "area-";
        AreaLabelSuggestion[] expected = [new($"{prefix}Test", 0.9)];
        await cache.SetAsync(
            $"AreaLabels:{OpenAIService.DefaultModel}:medium:dotnet/runtime:123:{prefix}", expected);
        await app.StartAsync();

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{address}/mcp") });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        var tool = Assert.Single(tools, tool => tool.Name == "predict_issue_labels");
        Assert.Contains(tools, tool => tool.Name == "search_dotnet_repos");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint);
        var schema = tool.JsonSchema;
        Assert.Equal("area-", schema.GetProperty("properties").GetProperty("labelPrefix").GetProperty("default").GetString());
        Assert.Equal(["repository", "number"], schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.False(schema.GetProperty("properties").TryGetProperty("cancellationToken", out _));

        Dictionary<string, object?> arguments = new() { ["repository"] = "DOTNET/RUNTIME", ["number"] = 123 };
        if (labelPrefix is not null)
        {
            arguments["labelPrefix"] = labelPrefix;
        }
        var result = await client.CallToolAsync("predict_issue_labels", arguments, cancellationToken: timeout.Token);

        Assert.False(result.IsError is true, JsonSerializer.Serialize(result));
        string json = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(expected, JsonSerializer.Deserialize<AreaLabelSuggestion[]>(json, JsonSerializerOptions.Web));
        Assert.Equal($"[MCP]: PredictIssueLabels for DOTNET/RUNTIME#123 (labelPrefix: {prefix})", Assert.Single(logs));

        arguments["number"] = 0;
        var error = await client.CallToolAsync("predict_issue_labels", arguments, cancellationToken: timeout.Token);
        Assert.True(error.IsError);
    }

    [Theory]
    [InlineData(null, 1, "area-")]
    [InlineData("", 1, "area-")]
    [InlineData(" ", 1, "area-")]
    [InlineData("dotnet/runtime", 0, "area-")]
    [InlineData("dotnet/runtime", -1, "area-")]
    [InlineData("dotnet/runtime", 1, null)]
    [InlineData("dotnet/runtime", 1, "")]
    [InlineData("dotnet/runtime", 1, " ")]
    public async Task InvalidArgumentsAreRejectedBeforePrediction(string? repository, int number, string? labelPrefix)
    {
        var server = new McpServer(Logger: null!, null!, null!);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => server.PredictIssueLabels(repository!, number, labelPrefix!));
    }

    [Fact]
    public async Task OversizedPrefixIsRejectedBeforePrediction()
    {
        var server = new McpServer(Logger: null!, null!, null!);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => server.PredictIssueLabels("dotnet/runtime", 1, new string('a', 101)));
    }
}
