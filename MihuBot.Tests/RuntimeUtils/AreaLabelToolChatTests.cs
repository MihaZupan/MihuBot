using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MihuBot.Helpers.RateLimiting;
using MihuBot.Helpers.AI;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.AI;
using ModelContextProtocol.Client;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelToolChatTests
{
    [Fact]
    public async Task ResponsesApiChainsResponseIdsWithinEachPredictionAndSendsOnlyNewToolResults()
    {
        await using var server = await TestMcpServer.StartAsync();
        await using var mcp = server.CreateClient();
        var tools = await mcp.GetToolsAsync(CancellationToken.None);
        int calls = 0;
        server.ResponsesHandler = async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            int call = calls++ % (AreaLabelToolChatClient.MaxToolRounds + 1) + 1;
            bool final = call == AreaLabelToolChatClient.MaxToolRounds + 1;
            Assert.Equal("/openai/v1/responses", context.Request.Path);
            Assert.Equal(OpenAIService.DefaultModel, request.RootElement.GetProperty("model").GetString());
            Assert.Equal(AreaLabelDetector.MaxOutputTokens, request.RootElement.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("medium", request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("json_schema", request.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.True(request.RootElement.GetProperty("store").GetBoolean());
            Assert.False(request.RootElement.TryGetProperty("conversation", out _));

            if (call > 1)
            {
                Assert.Equal($"resp_{calls - 1}", request.RootElement.GetProperty("previous_response_id").GetString());
                var input = Assert.Single(request.RootElement.GetProperty("input").EnumerateArray());
                Assert.Equal("function_call_output", input.GetProperty("type").GetString());
                Assert.Equal($"call-{calls - 1}", input.GetProperty("call_id").GetString());
            }
            else
            {
                Assert.False(request.RootElement.TryGetProperty("previous_response_id", out _));
                Assert.Equal("user", Assert.Single(request.RootElement.GetProperty("input").EnumerateArray()).GetProperty("role").GetString());
            }

            if (final)
            {
                Assert.True(!request.RootElement.TryGetProperty("tools", out var remaining) || remaining.GetArrayLength() == 0);
            }
            else
            {
                Assert.Equal(GitHubReadOnlyMcp.ToolNames.Length, request.RootElement.GetProperty("tools").GetArrayLength());
            }

            object[] output = final
                ? [
                    new
                    {
                        id = $"msg_{calls}", type = "message", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text = """{"data":[{"labelName":"area-Networking","confidence":0.9}]}""", annotations = Array.Empty<object>() } },
                    },
                ]
                : [
                    new { id = $"rs_{calls}", type = "reasoning", summary = Array.Empty<object>() },
                    new { id = $"fc_{calls}", type = "function_call", status = "completed", call_id = $"call-{calls}", name = "search_code", arguments = """{"query":"repo:dotnet/runtime Socket"}""" },
                ];
            await context.Response.WriteAsJsonAsync(new
            {
                id = $"resp_{calls}", @object = "response", created_at = 1, model = OpenAIService.DefaultModel, status = "completed", store = true,
                output,
                usage = new { input_tokens = 10, output_tokens = 3, total_tokens = 13 },
            });
        };
        using var limiter = new TokenRateLimiter(1_000_000, 1_000_000);
        List<string> logs = [];

        for (int prediction = 0; prediction < 2; prediction++)
        {
#pragma warning disable OPENAI001
            var model = OpenAIService.CreateResponsesClient(server.Endpoint, "test-token").AsIChatClient(OpenAIService.DefaultModel);
            var options = AreaLabelDetector.CreateChatOptions(new(OpenAIService.DefaultModel, OpenAI.Chat.ChatReasoningEffortLevel.Medium, true));
#pragma warning restore OPENAI001
            using var chat = CreateChatClient(new AreaLabelRateLimitedChatClient(model, limiter, logs.Add), _ => { });
            options.Tools = [.. tools];
            Assert.Null(options.ConversationId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await chat.GetResponseAsync<AreaLabelSuggestion[]>("Classify the issue.", options,
                useJsonSchemaResponseFormat: true, cancellationToken: timeout.Token);

            Assert.Equal(new AreaLabelSuggestion("area-Networking", 0.9), Assert.Single(result.Result));
            Assert.Equal(13 * (AreaLabelToolChatClient.MaxToolRounds + 1), result.Usage!.TotalTokenCount);
            Assert.Equal($"resp_{calls}", result.ConversationId);
            Assert.Contains(logs, message => message.EndsWith($"response ID: resp_{calls}", StringComparison.Ordinal));
        }

        Assert.Equal(2 * (AreaLabelToolChatClient.MaxToolRounds + 1), calls);
        Assert.Equal(2 * AreaLabelToolChatClient.MaxToolRounds, server.InvokedTools.Count);
    }

    [Fact]
    public async Task ToolFreePredictionsStillUseChatCompletionsWithConfiguredReasoning()
    {
        await using var server = await TestMcpServer.StartAsync();
        int calls = 0;
        server.ChatHandler = async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body);
            calls++;
            Assert.Equal(AreaLabelDetector.MaxOutputTokens, request.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.False(request.RootElement.TryGetProperty("tools", out _));
            await context.Response.WriteAsJsonAsync(new
            {
                id = "completion", @object = "chat.completion", created = 1, model = OpenAIService.DefaultModel,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = """{"data":[]}""" }, finish_reason = "stop" } },
            });
        };
        using var chat = new OpenAI.Chat.ChatClient(OpenAIService.DefaultModel, new System.ClientModel.ApiKeyCredential("test-token"),
            new OpenAI.OpenAIClientOptions { Endpoint = server.Endpoint }).AsIChatClient();
#pragma warning disable OPENAI001
        var options = AreaLabelDetector.CreateChatOptions(new(OpenAIService.DefaultModel, OpenAI.Chat.ChatReasoningEffortLevel.High, false));
#pragma warning restore OPENAI001
        var result = await chat.GetResponseAsync<AreaLabelSuggestion[]>("Classify.", options, useJsonSchemaResponseFormat: true);

        Assert.Empty(result.Result);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("https://resource.openai.azure.com", "https://resource.openai.azure.com/openai/v1/")]
    [InlineData("https://resource.openai.azure.com/", "https://resource.openai.azure.com/openai/v1/")]
    [InlineData("https://resource.openai.azure.com/openai", "https://resource.openai.azure.com/openai/v1/")]
    [InlineData("https://resource.openai.azure.com/openai/v1/", "https://resource.openai.azure.com/openai/v1/")]
    [InlineData("https://gateway.example/v1", "https://gateway.example/v1/")]
    [InlineData("https://gateway.example/prefix/openai/v1", "https://gateway.example/prefix/openai/v1/")]
    public void ResponsesEndpointUsesV1WithoutDuplicatingExistingPaths(string endpoint, string expected)
    {
        var client = OpenAIService.CreateResponsesClient(new Uri(endpoint), "test-token");
#pragma warning disable OPENAI001
        Assert.Equal(new Uri(expected), client.Endpoint);
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatefulRoundsReserveForStoredContextEvenWhenOnlyToolResultsAreSent(bool missingFirstUsage)
    {
        using var limiter = new TokenRateLimiter(1_000_000, 1_000_000);
        int calls = 0;
        int previousTokens = 0;
        using var chat = CreateChatClient(new AreaLabelRateLimitedChatClient(new TestChatClient(async (messages, options, ct) =>
        {
            if (calls++ == 0)
            {
                Assert.Null(options.ConversationId);
                previousTokens = missingFirstUsage
                    ? AreaLabelRateLimitedChatClient.EstimateTokenBudget(messages, options)
                    : 30_000;

                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "search_code", new Dictionary<string, object?>())]))
                {
                    ConversationId = "resp_first",
                    Usage = missingFirstUsage ? null : new() { InputTokenCount = 10_000, OutputTokenCount = 20_000, TotalTokenCount = 30_000 },
                };
            }

            Assert.Equal("resp_first", options.ConversationId);
            Assert.Single(Assert.Single(messages).Contents.OfType<FunctionResultContent>());
            int expectedBudget = previousTokens + AreaLabelRateLimitedChatClient.EstimateTokenBudget(messages, options);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var remaining = await limiter.ReserveAsync(1_000_000 - previousTokens - expectedBudget, timeout.Token);
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.ReserveAsync(1, probeTimeout.Token));
            }
            finally
            {
                remaining.Complete(0);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")) { ConversationId = "resp_final" };
        }), limiter, _ => { }), _ => { });
        var tool = AIFunctionFactory.Create(() => "Example", "search_code");
        await chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [tool] });

        Assert.Equal(2, calls);
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

        var error = await Assert.ThrowsAsync<AggregateException>(() => chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [tool] }));
        Assert.All(error.InnerExceptions, exception =>
        {
            Assert.IsType<InvalidOperationException>(exception);
            Assert.Contains($"exceeded {AreaLabelToolChatClient.MaxToolCalls}", exception.Message, StringComparison.Ordinal);
        });
        Assert.Equal(AreaLabelToolChatClient.MaxToolCalls, invocations);
    }

    [Fact]
    public async Task PersistentMcpErrorsAreRetriedThenFailThePrediction()
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
                [new FunctionCallContent($"call-{calls}", "search_code", new Dictionary<string, object?> { ["query"] = "socket" })])));
        }), _ => { });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [.. tools] }));

        Assert.Contains("returned an error", error.Message, StringComparison.Ordinal);
        Assert.InRange(calls, 2, AreaLabelToolChatClient.MaxToolRounds);
        Assert.Equal(calls, server.InvokedTools.Count);
    }

    [Fact]
    public async Task DefaultErrorHandlingAllowsTheModelToRecoverFromAToolFailure()
    {
        await using var server = await TestMcpServer.StartAsync();
        server.ToolError = true;
        await using var mcp = server.CreateClient();
        var tools = await mcp.GetToolsAsync(CancellationToken.None);
        int calls = 0;
        using var chat = CreateChatClient(new TestChatClient((messages, _, _) =>
        {
            calls++;

            if (calls == 2)
            {
                Assert.NotNull(Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()).Exception);
                server.ToolError = false;
            }

            return Task.FromResult(calls <= 2
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{calls}", "search_code", new Dictionary<string, object?> { ["query"] = "socket" })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        }), _ => { });
        using var defaults = new FunctionInvokingChatClient(new TestChatClient((_, _, _) => throw new NotSupportedException()));

        Assert.Equal(defaults.MaximumConsecutiveErrorsPerRequest, chat.MaximumConsecutiveErrorsPerRequest);
        var result = await chat.GetResponseAsync("Classify.", new ChatOptions { Tools = [.. tools] });

        Assert.Equal("[]", result.Messages.Last().Text);
        Assert.Equal(3, calls);
        Assert.Equal(2, server.InvokedTools.Count);
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
        public Func<HttpContext, Task>? ResponsesHandler { get; set; }
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
            app.MapPost("/openai/v1/responses", (HttpContext context) => server.ResponsesHandler!(context));
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
