namespace MihuBot.YoutubeArchive;

public sealed record YoutubeArchiveCookie(string Domain, string Path, string Name, string Value, bool Secure, bool HttpOnly, long? Expires = null);

public sealed record YoutubeArchiveRequest(string Url, string Mode, int? MaxHeight = null, YoutubeArchiveCookie[] Cookies = null);

public sealed record YoutubeArchiveMetadataRequest(string Url, YoutubeArchiveCookie[] Cookies = null);

public sealed record YoutubeArchiveJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string VideoId { get; init; }
    public string Mode { get; init; }
    public int? MaxHeight { get; init; }
    public string Status { get; init; } = "queued";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string File { get; init; }
    public string Error { get; init; }
    public string Warning { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProtectedCookies { get; init; }
}

public sealed class YoutubeArchiveState
{
    public List<YoutubeArchiveJob> Jobs { get; set; } = [];
}

public static class YoutubeArchiveValidation
{
    public static readonly int[] Heights = [360, 480, 720, 1080, 1440, 2160, 4320];

    public static bool IsValid(YoutubeArchiveRequest request) =>
        request is not null &&
        YoutubeHelper.TryParseVideoUrl(request.Url, out _) &&
        AreCookiesValid(request.Cookies) &&
        ((request.Mode == "audio" && request.MaxHeight is null) ||
         (request.Mode == "video" && (request.MaxHeight is null || Heights.Contains(request.MaxHeight.Value))));

    public static string GetUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    public static bool AreCookiesValid(YoutubeArchiveCookie[] cookies) =>
        cookies is null ||
        (cookies.Length <= 200 && cookies.All(cookie =>
            cookie is not null &&
            IsCookieField(cookie.Domain, 256) &&
            Uri.CheckHostName(cookie.Domain.TrimStart('.')) == UriHostNameType.Dns &&
            (cookie.Domain.TrimStart('.').Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             cookie.Domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)) &&
            IsCookieField(cookie.Path, 2048) && cookie.Path.StartsWith('/') &&
            IsCookieField(cookie.Name, 256) && cookie.Name.Length > 0 &&
            IsCookieField(cookie.Value, 4096) &&
            (cookie.Expires is null or >= 0)));

    private static bool IsCookieField(string value, int maxLength) =>
        value is not null && value.Length <= maxLength &&
        !value.AsSpan().ContainsAnyInRange('\0', '\x1f') && !value.Contains('\x7f');
}
