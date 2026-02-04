using System.Net.Http.Json;
using System.Text.Json;

namespace MihuBot.Helpers;

public sealed class QBittorrentClient
{
    private static readonly JsonSerializerOptions s_snakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;
    private readonly string _username;
    private readonly string _password;
    private bool _loggedIn;

    public QBittorrentClient(string url, string username, string password)
    {
        _client = new HttpClient();
        _client.BaseAddress = new Uri(url);
        _username = username;
        _password = password;
    }

    public async Task LoginAsync(CancellationToken ct = default)
    {
        if (_loggedIn)
        {
            return;
        }

        using HttpResponseMessage response = await MakeRequestAsync("/api/v2/auth/login", ct, ("username", _username), ("password", _password));
        response.EnsureSuccessStatusCode();
        _loggedIn = true;
    }

    public async Task<SearchResult[]> SearchAsync(string pattern, TimeSpan searchDelay, CancellationToken ct = default)
    {
        var start = await MakeRequestAsync<SearchStartResult>("/api/v2/search/start", JsonSerializerOptions.Web, ct, ("pattern", pattern), ("plugins", "all"), ("category", "all"));
        string id = start.Id.ToString();

        await Task.Delay(searchDelay, ct);

        using HttpResponseMessage stopResponse = await MakeRequestAsync("/api/v2/search/stop", ct, ("id", id));
        stopResponse.EnsureSuccessStatusCode();

        var results = await MakeRequestAsync<SearchResultsResult>("/api/v2/search/results", JsonSerializerOptions.Web, ct, ("id", id));

        return results.Results;
    }

    public async Task<TorrentInfo> GetTorrentInfoAsync(string hash, CancellationToken ct = default)
    {
        return await MakeRequestAsync<TorrentInfo>("/api/v2/torrents/properties", s_snakeCaseOptions, ct, ("hash", hash));
    }

    public async Task DeleteTorrentAsync(string hash, bool deleteFiles, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await MakeRequestAsync("/api/v2/torrents/delete", ct, ("hashes", hash), ("deleteFiles", deleteFiles.ToString()));
        response.EnsureSuccessStatusCode();
    }

    public async Task AddTorrentAsync(string url, string savePath, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await MakeRequestAsync("/api/v2/torrents/add", ct, ("urls", url), ("savepath", savePath));
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> MakeRequestAsync<T>(string api, JsonSerializerOptions jsonOptions, CancellationToken ct, params (string Key, string Value)[] parameters)
    {
        using HttpResponseMessage response = await MakeRequestAsync(api, ct, parameters);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(jsonOptions, ct);
    }

    private async Task<HttpResponseMessage> MakeRequestAsync(string api, CancellationToken ct, params (string Key, string Value)[] parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, api);
        request.Content = new FormUrlEncodedContent(parameters.Select(p => KeyValuePair.Create(p.Key, p.Value)));
        return await _client.SendAsync(request, ct);
    }

    private sealed record SearchStartResult(long Id);
    private sealed record SearchResultsResult(SearchResult[] Results, string Status, int Total);

    public sealed record SearchResult(string DescrLink, string FileName, long FileSize, string FileUrl, long NbLeechers, long NbSeeders, string SiteUrl);
    public sealed record TorrentInfo(
        string Hash, string InfohashV1, string InfohashV2, string Name, string Comment,
        long AdditionDate, long CompletionDate, long TimeElapsed, long LastSeen, long Eta,
        string CreatedBy, bool HasMetadata, bool IsPrivate,
        long DlLimit, long DlSpeed, long DlSpeedAvg, long UpLimit, long UpSpeed, long UpSpeedAvg,
        string DownloadPath, string SavePath,
        long NbConnections, long NbConnectionsLimit,
        long Peers, long PeersTotal, long PieceSize, long PiecesHave, long PiecesNum,
        long Popularity, long SeedingTime, long Seeds, long SeedsTotal, double ShareRatio,
        long TotalDownloaded, long TotalDownloadedSession, long TotalSize, long TotalUploaded, long TotalUploadedSession, long TotalWasted);
}
