namespace MihuBot.Helpers;

public sealed class JellyfinClient
{
    private readonly HttpClient _client;

    public JellyfinClient(string url, string apiKey)
    {
        _client = new HttpClient();
        _client.BaseAddress = new Uri(url);
        _client.DefaultRequestHeaders.Add("Authorization", $"MediaBrowser Token={apiKey}");
    }

    public async Task RefreshLibraryAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage response = await _client.PostAsync("/Library/Refresh", new ByteArrayContent([]), ct);
        response.EnsureSuccessStatusCode();
    }
}
