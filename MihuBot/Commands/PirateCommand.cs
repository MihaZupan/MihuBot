using System.Text.RegularExpressions;
using Discord.Rest;

#nullable enable

namespace MihuBot.Commands;

public sealed partial class PirateCommand : CommandBase
{
    public override string Command => "pirate";

    private readonly QBittorrentClient _qBittorrent;
    private readonly JellyfinClient _jellyfin;

    public PirateCommand(QBittorrentClient qBittorrent, JellyfinClient jellyfin)
    {
        _qBittorrent = qBittorrent;
        _jellyfin = jellyfin;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.Guild.Id is not (Guilds.Mihu or Guilds.PrivateLogs or Guilds.TheBoys))
        {
            return;
        }

        if (ctx.Arguments.Length == 0)
        {
            await ctx.ReplyAsync("Arrr! Ye need to give me somethin' to pirate!");
            return;
        }

        string query = ctx.ArgumentStringTrimmed;

        if (query.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadAsync(ctx, new MagnetUri(query));
            return;
        }

        ctx.Message.AddReactionAsync(Emotes.ThumbsUp).IgnoreExceptions();

        QBittorrentClient.SearchResult[] results;
        try
        {
            await _qBittorrent.LoginAsync(ctx.CancellationToken);

            results = await _qBittorrent.SearchAsync(query, TimeSpan.FromSeconds(5), ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            await ctx.ReplyAsync($"Search for '{query}' failed: {ex.Message}");
            await ctx.DebugAsync(ex);
            return;
        }

        QBittorrentClient.SearchResult? bestResult = TryGetBestResult(results, query);

        if (bestResult is null)
        {
            await ctx.ReplyAsync("No suitable results found.");
            return;
        }

        await DownloadAsync(ctx, new MagnetUri(bestResult.FileUrl));
    }

    private async Task DownloadAsync(CommandContext ctx, MagnetUri uri)
    {
        bool error = false;
        try
        {
            bool isSearies = SeasonRegex.IsMatch(uri.DisplayName);

            await _qBittorrent.LoginAsync(ctx.CancellationToken);
            await _qBittorrent.AddTorrentAsync(uri.Url, isSearies ? "/media/Shows" : "/media/Movies", ctx.CancellationToken);

            QBittorrentClient.TorrentInfo info;
            RestUserMessage? message = null;
            while (true)
            {
                info = await _qBittorrent.GetTorrentInfoAsync(uri.Hash, ctx.CancellationToken);

                if (info.CompletionDate > 0)
                {
                    break;
                }

                var embed = new EmbedBuilder()
                    .WithTitle("Downloading...")
                    .AddField("Name", info.Name, inline: false);

                if (info.HasMetadata)
                {
                    double progress = info.PiecesNum > 0 ? (double)info.PiecesHave / info.PiecesNum : 0.0;
                    string progressStr = $"{(int)(progress * 100)}%";
                    if (progressStr == "69%")
                    {
                        progressStr += " (Nice!)";
                    }

                    long speedKbps = info.DlSpeed / 1024;
                    string speed = speedKbps switch
                    {
                        >= 512 => $"{speedKbps / 1024}.{speedKbps % 1024 / 102} MB/s",
                        _ => $"{speedKbps} kB/s"
                    };

                    string eta = info.Eta switch
                    {
                        < 0 or >= TimeSpan.SecondsPerDay => "∞",
                        < 60 => "Less than a minute",
                        < 3600 => $"{info.Eta / 60} minutes",
                        _ => $"{info.Eta / 3600} hours"
                    };

                    embed = embed
                        .AddField("Size", SharedHelpers.GetRoughSizeString(info.TotalSize), inline: true)
                        .AddField("Progress", $"{progressStr} ({SharedHelpers.GetRoughSizeString(info.TotalDownloaded)})", inline: true)
                        .AddField("Download Speed", speed, inline: true)
                        .AddField("ETA", eta, inline: true)
                        .WithColor(r: 0, g: (int)(progress * 255), b: 0);
                }
                else
                {
                    embed = embed
                        .AddField("Status", "Fetching metadata...", inline: true);
                }

                if (message is null)
                {
                    message = await ctx.Channel.SendMessageAsync(embed: embed.Build());
                }
                else
                {
                    await message.ModifyAsync(m => { m.Content = null; m.Embed = embed.Build(); });
                }

                await Task.Delay(5000, ctx.CancellationToken);
            }

            await _jellyfin.RefreshLibraryAsync(ctx.CancellationToken);

            if (message is not null)
            {
                await message.DeleteAsync();
            }

            await ctx.ReplyAsync($"Downloaded `{info.Name}` successfully.", mention: true);
        }
        catch (Exception ex)
        {
            error = true;
            await ctx.ReplyAsync($"Failed to add torrent: {ex.Message}");
            await ctx.DebugAsync(ex);
        }
        finally
        {
            try
            {
                await _qBittorrent.DeleteTorrentAsync(uri.Hash, deleteFiles: error, ctx.CancellationToken);
            }
            catch (Exception ex)
            {
                await ctx.DebugAsync(ex, $"Failed to delete torrent {uri.Hash}");
            }
        }
    }

    private static QBittorrentClient.SearchResult? TryGetBestResult(QBittorrentClient.SearchResult[] results, string name)
    {
        long minSize = 10L * 1024 * 1024; // 10 MB

        // 50 GB for series season, 4 GB for episode, 16 GB for movies
        long maxSize = SeasonRegex.IsMatch(name)
            ? (EpisodeRegex.IsMatch(name) ? 4L * 1024 * 1024 * 1024 : 50L * 1024 * 1024 * 1024)
            : 16L * 1024 * 1024 * 1024;

        results = [.. results
            .Where(r => r.FileUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            .Where(r => !IsHdr(r.FileName))
            .Where(r => r.NbSeeders >= 3)
            .Where(r => r.FileSize >= minSize && r.FileSize <= maxSize)
            .OrderByDescending(r => r.NbSeeders)];

        return
            TryGetYtsHevc4k(results) ??
            TryGetYts4k(results) ??
            TryGetHevc4k(results) ??
            TryGet4k(results) ??
            TryGetHevc(results) ??
            TryGetYts(results) ??
            results.FirstOrDefault();

        static QBittorrentClient.SearchResult? TryGetYtsHevc4k(QBittorrentClient.SearchResult[] results) =>
            results.FirstOrDefault(r => IsHevc(r.FileName) && Is4k(r.FileName) && IsYtsMx(r.FileName));

        static QBittorrentClient.SearchResult? TryGetHevc4k(QBittorrentClient.SearchResult[] results) =>
            results.FirstOrDefault(r => IsHevc(r.FileName) && Is4k(r.FileName));

        static QBittorrentClient.SearchResult? TryGetYts4k(QBittorrentClient.SearchResult[] results) =>
            results.FirstOrDefault(r => Is4k(r.FileName) && IsYtsMx(r.FileName));

        static QBittorrentClient.SearchResult? TryGet4k(QBittorrentClient.SearchResult[] results) =>
            results.FirstOrDefault(r => Is4k(r.FileName));

        static QBittorrentClient.SearchResult? TryGetHevc(QBittorrentClient.SearchResult[] results) =>
            results.FirstOrDefault(r => IsHevc(r.FileName));

        static QBittorrentClient.SearchResult? TryGetYts(QBittorrentClient.SearchResult[] results) =>
            results.FirstOrDefault(r => IsYtsMx(r.FileName));

        static bool IsHevc(string fileName) =>
            fileName.Contains("HEVC", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("x265", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("h265", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".265", StringComparison.OrdinalIgnoreCase);

        static bool Is4k(string fileName) =>
            fileName.Contains("[4K]", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(" 4K ", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".4K", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("2160p", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".UHD", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(" UHD", StringComparison.OrdinalIgnoreCase);

        static bool IsYtsMx(string fileName) =>
            fileName.Contains("YTS.MX", StringComparison.OrdinalIgnoreCase);

        static bool IsHdr(string fileName) =>
            fileName.Contains(" HDR ", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".HDR", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("HDR10", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\WS ?\d{1,3}|Season ?\d{1,3}", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex { get; }

    [GeneratedRegex(@"\WEp? ?\d{1,3}|Episode ?\d{1,3}", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex { get; }
}
