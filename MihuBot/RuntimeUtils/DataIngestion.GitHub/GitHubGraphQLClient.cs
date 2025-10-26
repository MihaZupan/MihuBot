using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

#nullable enable

#pragma warning disable CA1873 // Avoid potentially expensive logging

public sealed class GithubGraphQLClient(string productName, string[] tokens, ILogger logger)
{
    private readonly HttpClient _http = new();
    private readonly Uri _apiUrl = new("https://api.github.com/graphql");

    public async Task<T> RunQueryAsync<T>(string query, object variables, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
        {
            Content = JsonContent.Create(new { query, variables }),
            Version = HttpVersion.Version20
        };

        request.Headers.Add("User-Agent", productName);
        request.Headers.Add("Authorization", $"Bearer {tokens.Random()}");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);

        logger.LogDebug("GitHub GraphQL response status: {StatusCode}", response.StatusCode);

        response.EnsureSuccessStatusCode();

        if (logger.IsEnabled(LogLevel.Trace))
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogTrace("GitHub GraphQL response: {Response}", json);
        }

        Response<T>? responseData = await response.Content.ReadFromJsonAsync<Response<T>>(cancellationToken);
        return responseData!.Data!;
    }

    private sealed class Response<T>
    {
        public T? Data { get; set; }
    }
}
