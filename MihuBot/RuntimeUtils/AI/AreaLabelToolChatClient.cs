using System.Text.Json;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;

namespace MihuBot.RuntimeUtils.AI;

internal sealed class AreaLabelToolChatClient : FunctionInvokingChatClient
{
    internal const int MaxToolRounds = 6;
    internal const int MaxToolCalls = 20;
    internal const int MaxToolResultCharacters = 24_000;

    private readonly Action<string> _log;
    private readonly AreaLabelToolDataFilter _filter;
    private int _toolCalls;

    public AreaLabelToolChatClient(IChatClient innerClient, Action<string> log, IssueInfo filteredTarget = null) : base(innerClient)
    {
        _log = log;
        _filter = filteredTarget is null ? null : new(filteredTarget);
        MaximumIterationsPerRequest = MaxToolRounds;
        MaximumConsecutiveErrorsPerRequest = 0;
        AllowConcurrentInvocation = true;
    }

    protected override async ValueTask<object> InvokeFunctionAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int call = Interlocked.Increment(ref _toolCalls);

        if (call > MaxToolCalls)
        {
            throw new InvalidOperationException($"Area label prediction exceeded {MaxToolCalls} GitHub tool calls.");
        }

        _log($"Area label GitHub tool call {call}: {context.Function.Name}");

        if (_filter?.IsBlockedRequest(context.Function.Name, context.Arguments) is true)
        {
            _log($"Blocked area label target lookup via {context.Function.Name}.");

            return """{"error":"Target-item lookups are blocked to avoid label leakage. Use the supplied target context and investigate other issues or source files instead."}""";
        }

        object result = await base.InvokeFunctionAsync(context, cancellationToken);

        if (result is JsonElement { ValueKind: JsonValueKind.Object } json &&
            json.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException($"GitHub MCP tool {context.Function.Name} returned an error: {json.GetRawText().TruncateWithDotDotDot(2000)}");
        }

        string text = _filter is null ? JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions) :
            _filter.FilterResponse(context.Function.Name, context.Arguments, result);

        return text.Length <= MaxToolResultCharacters ? text :
            text[..MaxToolResultCharacters] + "\n[Tool result truncated. Request a smaller page, narrower query, or a specific file.]";
    }
}

internal sealed class AreaLabelRateLimitedChatClient(
    IChatClient innerClient,
    TokenRateLimiter rateLimiter,
    Action<string> log) : DelegatingChatClient(innerClient)
{
    private int _modelCalls;

    internal static int EstimateTokenBudget(IEnumerable<ChatMessage> messages, ChatOptions options) =>
        AreaLabelDetector.EstimateTokenBudget(JsonSerializer.Serialize(new
        {
            Messages = messages,
            Tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(t => new { t.Name, t.Description, Parameters = t.JsonSchema }),
        }, AIJsonUtilities.DefaultOptions));

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
    {
        if (++_modelCalls > AreaLabelToolChatClient.MaxToolRounds + 1)
        {
            throw new InvalidOperationException("Area label prediction exceeded its model call budget.");
        }

        ChatMessage[] history = [.. messages];
        var reservation = await rateLimiter.ReserveAsync(EstimateTokenBudget(history, options), cancellationToken);
        var response = await base.GetResponseAsync(history, options, cancellationToken);

        if (AreaLabelDetector.GetActualTokenCount(response.Usage) is { } actualTokens)
        {
            reservation.Complete(actualTokens);
        }
        else
        {
            log($"Area label model call {_modelCalls} returned no usable token usage; keeping the full token reservation.");
        }

        return response;
    }
}
