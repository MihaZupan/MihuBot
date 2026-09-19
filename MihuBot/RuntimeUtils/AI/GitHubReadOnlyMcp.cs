using ModelContextProtocol.Client;

namespace MihuBot.RuntimeUtils.AI;

public sealed class GitHubReadOnlyMcp : IAsyncDisposable
{
    internal const string Endpoint = "https://api.githubcopilot.com/mcp/readonly";
    internal static readonly string[] ToolNames =
    [
        "search_issues", "issue_read", "list_issues",
        "search_pull_requests", "pull_request_read", "list_pull_requests",
        "search_code", "get_file_contents", "get_commit", "list_commits", "search_commits",
        "search_repositories",
    ];

    private readonly HttpClientTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient _client;
    private McpClientTool[] _tools;

    public GitHubReadOnlyMcp(IConfiguration configuration)
        : this(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = CreateHeaders(configuration["GitHub:Token"]),
        })
    {
    }

    internal GitHubReadOnlyMcp(HttpClientTransportOptions options) => _transport = new(options);

    internal static Dictionary<string, string> CreateHeaders(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new()
        {
            ["Authorization"] = $"Bearer {token}",
            ["X-MCP-Tools"] = string.Join(',', ToolNames),
        };
    }

    internal async Task<IReadOnlyList<McpClientTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_tools is not null)
            {
                return _tools;
            }

            var client = await McpClient.CreateAsync(_transport, cancellationToken: cancellationToken);

            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                var allowed = tools
                    .Where(t => ToolNames.Contains(t.Name, StringComparer.Ordinal) && t.ProtocolTool.Annotations?.ReadOnlyHint is true)
                    .ToArray();

                if (allowed.Length != ToolNames.Length || allowed.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != ToolNames.Length)
                {
                    throw new InvalidOperationException("GitHub MCP did not expose the expected read-only tools.");
                }

                _client = client;
                _tools = allowed;

                return _tools;
            }
            catch
            {
                await client.DisposeAsync();

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }
        }
        finally
        {
            await _transport.DisposeAsync();
            _gate.Dispose();
        }
    }
}
