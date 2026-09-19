using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MihuBot.Helpers.RateLimiting;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.AI;
using ModelContextProtocol.Client;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelToolChatTests
{
    [Fact]
    public async Task OpenAIAdapterDoesNotAccumulateToolsAndRemovesThemForTheFinalRequest()
    {
        await using var server = await TestMcpServer.StartAsync();
        await using var mcp = server.CreateClient();
        var tools = await mcp.GetToolsAsync(CancellationToken.None);
        int calls = 0;
        server.ChatHandler = async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            int call = ++calls;
            bool final = call == AreaLabelToolChatClient.MaxToolRounds + 1;
            Assert.Equal(AreaLabelDetector.MaxOutputTokens, request.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.Equal("medium", request.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.Equal("json_schema", request.RootElement.GetProperty("response_format").GetProperty("type").GetString());

            if (final)
            {
                Assert.False(request.RootElement.TryGetProperty("tools", out _));
            }
            else
            {
                Assert.Equal(GitHubReadOnlyMcp.ToolNames.Length, request.RootElement.GetProperty("tools").GetArrayLength());
            }

            object message = final
                ? new { role = "assistant", content = """{"data":[{"labelName":"area-Networking","confidence":0.9}]}""" }
                : new
                {
                    role = "assistant",
                    tool_calls = new[]
                    {
                        new { id = $"call-{call}", type = "function", function = new { name = "search_code", arguments = """{"query":"repo:dotnet/runtime Socket"}""" } },
                    },
                };
            await context.Response.WriteAsJsonAsync(new
            {
                id = $"completion-{call}", @object = "chat.completion", created = 1, model = "test-model",
                choices = new[] { new { index = 0, message, finish_reason = final ? "stop" : "tool_calls" } },
                usage = new { prompt_tokens = 10, completion_tokens = 3, total_tokens = 13 },
            });
        };
        var model = new OpenAI.Chat.ChatClient("test-model", new System.ClientModel.ApiKeyCredential("test-token"),
            new OpenAI.OpenAIClientOptions { Endpoint = server.Endpoint }).AsIChatClient();
        using var limiter = new TokenRateLimiter(1_000_000, 1_000_000);
        using var chat = CreateChatClient(new AreaLabelRateLimitedChatClient(model, limiter, _ => { }), _ => { });
#pragma warning disable OPENAI001
        var options = AreaLabelDetector.CreateChatOptions(new("test-model", OpenAI.Chat.ChatReasoningEffortLevel.Medium, true));
#pragma warning restore OPENAI001
        options.Tools = [.. tools];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await chat.GetResponseAsync<AreaLabelSuggestion[]>("Classify the issue.", options,
            useJsonSchemaResponseFormat: true, cancellationToken: timeout.Token);

        Assert.Equal(new AreaLabelSuggestion("area-Networking", 0.9), Assert.Single(result.Result));
        Assert.Equal(AreaLabelToolChatClient.MaxToolRounds + 1, calls);
        Assert.Equal(AreaLabelToolChatClient.MaxToolRounds, server.InvokedTools.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMcpToolsFeedTheStructuredPredictionAndEveryModelCallIsCharged(bool missingFirstUsage)
    {
        await using var server = await TestMcpServer.StartAsync();
        await using var mcp = server.CreateClient();
        var tools = await mcp.GetToolsAsync(CancellationToken.None);
        using var limiter = new TokenRateLimiter(1_000_000, 1_000_000);
        List<int> estimates = [];
        List<string> logs = [];
        int calls = 0;
        using var chat = CreateChatClient(new AreaLabelRateLimitedChatClient(new TestChatClient((messages, options, ct) =>
        {
            estimates.Add(AreaLabelRateLimitedChatClient.EstimateTokenBudget(messages, options));
            Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);

            if (calls++ == 0)
            {
                Assert.Equal(GitHubReadOnlyMcp.ToolNames.Order(), options.Tools!.Select(t => t.Name).Order());

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new TextContent("Checking the source before deciding."),
                     new FunctionCallContent("call-1", "search_code", new Dictionary<string, object?> { ["query"] = "repo:dotnet/runtime socket" })]))
                {
                    Usage = missingFirstUsage ? null : new() { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 },
                });
            }

            var result = Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
            Assert.Contains("Matching socket implementation", Assert.IsType<string>(result.Result), StringComparison.Ordinal);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"data":[{"labelName":"area-Networking","confidence":0.9}]}"""))
            {
                Usage = new() { InputTokenCount = 20, OutputTokenCount = 3, TotalTokenCount = 23 },
            });
        }), limiter, logs.Add), logs.Add);

        var prediction = await chat.GetResponseAsync<AreaLabelSuggestion[]>("Classify the socket issue.",
            new ChatOptions { Tools = [.. tools], MaxOutputTokens = AreaLabelDetector.MaxOutputTokens },
            useJsonSchemaResponseFormat: true);

        Assert.Equal(new AreaLabelSuggestion("area-Networking", 0.9), Assert.Single(prediction.Result));
        Assert.Equal(2, calls);
        Assert.Equal(["search_code"], server.InvokedTools);
        Assert.Equal(missingFirstUsage ? 23 : 35, prediction.Usage!.TotalTokenCount);
        Assert.True(estimates[1] > estimates[0]);
        Assert.Equal(missingFirstUsage, logs.Any(l => l.Contains("no usable token usage", StringComparison.Ordinal)));

        int charged = missingFirstUsage ? estimates[0] + 23 : 35;
        await limiter.ReserveAsync(1_000_000 - charged, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.ReserveAsync(1, timeout.Token));
    }

    [Fact]
    public async Task ToolRoundsEndWithAToolFreeFinalPrediction()
    {
        int calls = 0, invocations = 0;
        var tool = AIFunctionFactory.Create(() => { invocations++; return "Example"; }, "search_code");
        using var limiter = new TokenRateLimiter(1_000_000, 1_000_000);
        using var chat = CreateChatClient(new AreaLabelRateLimitedChatClient(new TestChatClient((_, options, _) =>
        {
            calls++;

            if (calls <= AreaLabelToolChatClient.MaxToolRounds)
            {
                Assert.Contains(tool, options.Tools!);

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{calls}", tool.Name, new Dictionary<string, object?>())])));
            }

            Assert.True(options.Tools is null || options.Tools.Count == 0);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"data":[]}""")));
        }), limiter, _ => { }), _ => { });

        var prediction = await chat.GetResponseAsync<AreaLabelSuggestion[]>("Classify.",
            new ChatOptions { Tools = [tool] }, useJsonSchemaResponseFormat: true);

        Assert.Empty(prediction.Result);
        Assert.Equal(AreaLabelToolChatClient.MaxToolRounds + 1, calls);
        Assert.Equal(AreaLabelToolChatClient.MaxToolRounds, invocations);
    }

    [Fact]
    public async Task TooManyCallsInOneRoundFailRatherThanExecuteUnboundedTools()
    {
        int invocations = 0;
        var tool = AIFunctionFactory.Create(() => { Interlocked.Increment(ref invocations); return "Example"; }, "search_code");
        using var chat = CreateChatClient(new TestChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                Enumerable.Range(0, AreaLabelToolChatClient.MaxToolCalls + 1)
                    .Select(i => (AIContent)new FunctionCallContent($"call-{i}", tool.Name, new Dictionary<string, object?>())).ToList())))), _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [tool] }));
        Assert.Equal(AreaLabelToolChatClient.MaxToolCalls, invocations);
    }

    [Fact]
    public async Task McpErrorsFailThePredictionInsteadOfBecomingSuccessShapedEvidence()
    {
        await using var server = await TestMcpServer.StartAsync();
        server.ToolError = true;
        await using var mcp = server.CreateClient();
        var tools = await mcp.GetToolsAsync(CancellationToken.None);
        int calls = 0;
        using var chat = CreateChatClient(new TestChatClient((_, _, _) =>
        {
            calls++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "search_code", new Dictionary<string, object?> { ["query"] = "socket" })])));
        }), _ => { });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [.. tools] }));

        Assert.Contains("returned an error", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.Single(server.InvokedTools);
    }

    [Fact]
    public async Task ToolResultsAreBoundedAndExplicitlyMarkedWhenTruncated()
    {
        var tool = AIFunctionFactory.Create(() => new string('x', AreaLabelToolChatClient.MaxToolResultCharacters * 2), "get_file_contents");
        int calls = 0;
        using var chat = CreateChatClient(new TestChatClient((messages, _, _) =>
        {
            if (calls++ == 0)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?>())])));
            }

            string text = Assert.IsType<string>(Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()).Result);
            Assert.InRange(text.Length, AreaLabelToolChatClient.MaxToolResultCharacters, AreaLabelToolChatClient.MaxToolResultCharacters + 150);
            Assert.Contains("[Tool result truncated.", text, StringComparison.Ordinal);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        }), _ => { });

        await chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [tool] });
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationStopsToolExecutionWithoutAnotherModelCall()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = AIFunctionFactory.Create((CancellationToken ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();

            return "Should not complete";
        }, "search_code");
        int calls = 0;
        using var chat = CreateChatClient(new TestChatClient((_, _, _) =>
        {
            calls++;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?>())])));
        }), _ => { });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [tool] }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DiscoveryIsSharedAndDoesNotExposeUnexpectedTools()
    {
        await using var server = await TestMcpServer.StartAsync();
        await using var mcp = server.CreateClient();
        var lists = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => mcp.GetToolsAsync(CancellationToken.None)));

        Assert.All(lists, tools => Assert.Same(lists[0], tools));
        Assert.Equal(GitHubReadOnlyMcp.ToolNames.Order(), lists[0].Select(t => t.Name).Order());
        Assert.Contains(lists[0], t => t.Name == "search_commits");
        Assert.DoesNotContain(lists[0], t => t.Name == "search_users");
        Assert.DoesNotContain(lists[0], t => t.Name == "issue_write");
        Assert.Equal(1, server.DiscoveryCalls);
        Assert.Equal("Bearer test-token", server.Authorization);
        Assert.Equal(string.Join(',', GitHubReadOnlyMcp.ToolNames), server.RequestedTools);
    }

    [Fact]
    public async Task DiscoveryRejectsNonReadOnlyToolsAndCanRetryAfterFailure()
    {
        await using var server = await TestMcpServer.StartAsync();
        server.UnsafeTool = true;
        await using var mcp = server.CreateClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => mcp.GetToolsAsync(CancellationToken.None));

        server.UnsafeTool = false;
        var tools = await mcp.GetToolsAsync(CancellationToken.None);
        Assert.Equal(GitHubReadOnlyMcp.ToolNames.Length, tools.Count);
        Assert.Equal(2, server.DiscoveryCalls);
    }

    [Fact]
    public async Task IndependentToolCallsActuallyRunConcurrently()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0, calls = 0;
        var tool = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(ct);

            return "Example";
        }, "search_code");
        using var chat = CreateChatClient(new TestChatClient((_, _, _) =>
            Task.FromResult(calls++ == 0
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?>()),
                     new FunctionCallContent("call-2", tool.Name, new Dictionary<string, object?>())]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")))), _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(chat.AllowConcurrentInvocation);
        Assert.Equal(20, AreaLabelToolChatClient.MaxToolCalls);
        await chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [tool] }, timeout.Token);

        Assert.Equal(2, started);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetRequestsAreOnlyBlockedDuringBacktests(bool filterTargetData)
    {
        int invocations = 0, calls = 0;
        var tool = AIFunctionFactory.Create(() => { invocations++; return "Target socket issue labeled area-Current"; }, "issue_read");
        using var chat = CreateChatClient(new TestChatClient((messages, _, _) =>
        {
            if (calls++ == 0)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?>
                    {
                        ["owner"] = "dotnet", ["repo"] = "runtime", ["issue_number"] = 123,
                    })])));
            }

            var result = Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
            Assert.Contains(filterTargetData ? "blocked" : "area-Current", Assert.IsType<string>(result.Result), StringComparison.Ordinal);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        }), _ => { }, filterTargetData);

        await chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [tool] });
        Assert.Equal(filterTargetData ? 0 : 1, invocations);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMcpSearchResponsesFilterTargetDataOnlyForBacktests(bool filterTargetData)
    {
        await using var server = await TestMcpServer.StartAsync();
        server.ToolText = """
            {"total_count":2,"items":[
                {"number":123,"html_url":"https://github.com/dotnet/runtime/issues/123","labels":["area-SecretAnswer"]},
                {"number":456,"html_url":"https://github.com/dotnet/runtime/issues/456","labels":["area-OtherExample"]}
            ]}
            """;
        await using var mcp = server.CreateClient();
        var tools = await mcp.GetToolsAsync(CancellationToken.None);
        int calls = 0;
        using var chat = CreateChatClient(new TestChatClient((messages, _, _) =>
        {
            if (calls++ == 0)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "search_issues", new Dictionary<string, object?> { ["query"] = "repo:dotnet/runtime Socket" })])));
            }

            string result = Assert.IsType<string>(Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()).Result);
            Assert.Equal(!filterTargetData, result.Contains("SecretAnswer", StringComparison.Ordinal));
            Assert.Equal(!filterTargetData, result.Contains("total_count", StringComparison.Ordinal));
            Assert.Contains("OtherExample", result, StringComparison.Ordinal);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        }), _ => { }, filterTargetData);

        await chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [.. tools] });
        Assert.Equal(["search_issues"], server.InvokedTools);
    }

    private static AreaLabelToolChatClient CreateChatClient(IChatClient innerClient, Action<string> log, bool filterTargetData = false) =>
        new(innerClient, log, filterTargetData ? new IssueInfo
        {
            Number = 123, Id = "I_target", Title = "Target socket issue",
            Repository = new RepositoryInfo { FullName = "dotnet/runtime" },
        } : null);

    private sealed class TestChatClient(Func<ChatMessage[], ChatOptions, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            respond([.. messages], options ?? new(), cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestMcpServer(WebApplication app) : IAsyncDisposable
    {
        public Uri Endpoint => new(app.Urls.Single());
        public Func<HttpContext, Task>? ChatHandler { get; set; }
        public bool UnsafeTool { get; set; }
        public bool ToolError { get; set; }
        public int DiscoveryCalls { get; private set; }
        public ConcurrentQueue<string> InvokedTools { get; } = new();
        public string ToolText { get; set; } = "Matching socket implementation";
        public string? Authorization { get; private set; }
        public string? RequestedTools { get; private set; }

        public GitHubReadOnlyMcp CreateClient() => new(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{app.Urls.Single()}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = GitHubReadOnlyMcp.CreateHeaders("test-token"),
        });

        public static async Task<TestMcpServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var server = new TestMcpServer(app);
            app.MapPost("/mcp", server.HandleAsync);
            app.MapPost("/chat/completions", (HttpContext context) => server.ChatHandler!(context));
            await app.StartAsync();

            return server;
        }

        private async Task HandleAsync(HttpContext context)
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);

            if (!request.RootElement.TryGetProperty("id", out var id))
            {
                context.Response.StatusCode = 202;

                return;
            }

            Authorization = context.Request.Headers.Authorization;
            RequestedTools = context.Request.Headers["X-MCP-Tools"];
            string method = request.RootElement.GetProperty("method").GetString()!;
            object result;

            if (method == "initialize")
            {
                result = new
                {
                    protocolVersion = request.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "test-github", version = "1.0" },
                };
            }
            else if (method == "tools/list")
            {
                DiscoveryCalls++;
                result = new
                {
                    tools = GitHubReadOnlyMcp.ToolNames.Concat(["issue_write", "search_users"]).Select(name => new
                    {
                        name,
                        description = "GitHub test tool",
                        inputSchema = new { type = "object", properties = new { query = new { type = "string" } } },
                        annotations = new { readOnlyHint = !(UnsafeTool && name == "search_code") },
                    }),
                };
            }
            else if (method == "tools/call")
            {
                InvokedTools.Enqueue(request.RootElement.GetProperty("params").GetProperty("name").GetString()!);
                result = new { content = new[] { new { type = "text", text = ToolError ? "GitHub unavailable" : ToolText } }, isError = ToolError };
            }
            else
            {
                await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, error = new { code = -32601, message = $"Unsupported method: {method}" } });

                return;
            }

            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result });
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
