using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

#nullable enable

#pragma warning disable CA1873 // Avoid potentially expensive logging

public sealed class GithubGraphQLClient(string productName, string[] tokens, ILogger logger)
{
    private readonly HttpClient _http = new();
    private readonly Uri _apiUrl = new("https://api.github.com/graphql");

    internal GithubGraphQLClient(string productName, string[] tokens, ILogger logger, HttpClient http)
        : this(productName, tokens, logger)
    {
        _http = http;
    }

    public async Task<T> RunQueryAsync<T>(string query, object variables, CancellationToken cancellationToken = default)
    {
        var response = await RunQueryWithErrorsAsync<T>(query, variables, cancellationToken);

        if (response.Errors is { Length: > 0 })
        {
            throw new InvalidOperationException($"GitHub GraphQL query failed: {string.Join("; ", response.Errors.Select(e => e.Message))}");
        }

        T? data = response.Data;

        if (!response.HasData || data is null || data is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            throw new InvalidOperationException("GitHub GraphQL returned no data.");
        }

        return data;
    }

    internal async Task<AliasedResponse<T>> RunAliasedQueryAsync<T>(
        string query, object variables, IReadOnlyCollection<string> aliases, CancellationToken cancellationToken = default) where T : class
    {
        var response = await RunQueryWithErrorsAsync<Dictionary<string, JsonElement>>(query, variables, cancellationToken);
        Error[] errors = response.Errors ?? [];
        HashSet<string> fields = new(aliases, StringComparer.Ordinal);
        Error[] globalErrors = [.. errors.Where(e => e.RootField != "rateLimit" && (e.RootField is null || !fields.Contains(e.RootField)))];
        Dictionary<string, FieldResult<T>> results = new(StringComparer.Ordinal);

        foreach (string alias in aliases)
        {
            Error[] fieldErrors = [.. globalErrors, .. errors.Where(e => e.RootField == alias)];

            if (fieldErrors.Length > 0)
            {
                results.Add(alias, new(null, fieldErrors, null));
            }
            else if (response.Data is null || !response.Data.TryGetValue(alias, out JsonElement value))
            {
                results.Add(alias, new(null, [], $"GitHub GraphQL returned no field '{alias}'."));
            }
            else
            {
                try
                {
                    results.Add(alias, new(value.Deserialize<T>(JsonSerializerOptions.Web), [], null));
                }
                catch (JsonException ex)
                {
                    results.Add(alias, new(null, [], $"Invalid GitHub GraphQL field '{alias}': {ex.Message}"));
                }
            }
        }

        RateLimitInfo? rateLimit = null;
        List<string> rateLimitErrors = [.. errors.Where(e => e.RootField == "rateLimit").Select(e => e.Message)];

        if (rateLimitErrors.Count == 0 && response.Data?.TryGetValue("rateLimit", out JsonElement rateLimitValue) == true)
        {
            try
            {
                rateLimit = rateLimitValue.Deserialize<RateLimitInfo>(JsonSerializerOptions.Web);
            }
            catch (JsonException ex)
            {
                rateLimitErrors.Add($"Invalid rate-limit metadata: {ex.Message}");
            }
        }

        return new(results, rateLimit, [.. rateLimitErrors]);
    }

    private async Task<Response<T>> RunQueryWithErrorsAsync<T>(string query, object variables, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
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

        return await response.Content.ReadFromJsonAsync<Response<T>>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub GraphQL returned an empty response.");
    }

    private sealed class Response<T>
    {
        public T? Data
        {
            get;
            set
            {
                field = value;
                HasData = true;
            }
        }

        [JsonIgnore]
        public bool HasData { get; private set; }

        public Error[] Errors { get; set; } = [];
    }

    internal sealed record Error(string Message, JsonElement[]? Path, string? Type = null)
    {
        public string? RootField => Path is [var root, ..] && root.ValueKind == JsonValueKind.String ? root.GetString() : null;
    }

    internal sealed record FieldResult<T>(T? Data, Error[] Errors, string? InvalidData) where T : class;

    internal sealed record AliasedResponse<T>(Dictionary<string, FieldResult<T>> Fields, RateLimitInfo? RateLimit, string[] RateLimitErrors) where T : class;

    internal sealed record RateLimitInfo([property: JsonRequired] int Cost, int? Remaining, DateTimeOffset? ResetAt);
}
