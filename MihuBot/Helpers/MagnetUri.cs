using System.Collections.Specialized;
using System.Web;

namespace MihuBot.Helpers;

public sealed class MagnetUri
{
    public string Url { get; }
    public string Hash { get; }
    public string DisplayName { get; }
    public string[] Trackers { get; }

    public MagnetUri(string url)
    {
        Url = url;

        var uri = new Uri(url);
        ArgumentOutOfRangeException.ThrowIfNotEqual(uri.Scheme, "magnet");

        NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
        string xt = query["xt"] ?? throw new ArgumentException("Missing xt argument");

        string[] hashes = xt.Split(',');

        string entry =
            hashes.FirstOrDefault(h => h.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase)) ??
            hashes.FirstOrDefault(h => h.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("Missing BT hash argument");

        Hash = entry.Split(':')[2];
        DisplayName = query["dn"];
        Trackers = query["tr"]?.Split(',') ?? [];
    }
}
