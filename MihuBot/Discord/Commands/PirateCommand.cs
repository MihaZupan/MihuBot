using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Discord.Rest;
using Microsoft.Extensions.AI;
using MihuBot.Discord;
using MihuBot.Helpers.AI;
using MihuBot.Helpers.Torrent;

#nullable enable

namespace MihuBot.Commands;

public sealed partial class PirateCommand : CommandBase
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MinTorrentSize = 10L * 1024 * 1024;
    private const long MaxTorrentSize = 16 * GB;

    /// <summary>Upper bound on how many results we're willing to describe to the model.</summary>
    private const int MaxCandidatesForAI = 25;

    /// <summary>Discord select menus allow at most 25 options, but a shorter list is much easier to pick from.</summary>
    private const int MaxOptionsToPresent = 10;

    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromMinutes(5);

    public override string Command => "pirate";

    private readonly QBittorrentClient _qBittorrent;
    private readonly JellyfinClient _jellyfin;
    private readonly OpenAIService? _openAI;
    private readonly ConcurrentDictionary<string, PendingSelection> _pendingSelections = [];
    private int _activeDownloads;

    public PirateCommand(QBittorrentClient qBittorrent, JellyfinClient jellyfin, OpenAIService? openAI = null)
    {
        _qBittorrent = qBittorrent;
        _jellyfin = jellyfin;
        _openAI = openAI;
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
            var magnet = new MagnetUri(query);

            if (!string.IsNullOrEmpty(magnet.DisplayName) && SeriesRegex.IsMatch(magnet.DisplayName))
            {
                await ctx.ReplyAsync($"`{magnet.DisplayName}` looks like a TV series. Only movies are supported.");
                return;
            }

            await DownloadAsync(ctx, magnet);
            return;
        }

        if (StringHelpers.TryGetArgument(ctx.ArgumentStringTrimmed, "filter", out string? filter))
        {
            query = query.Substring(0, query.IndexOf("-filter", StringComparison.OrdinalIgnoreCase));
        }

        if (StringHelpers.TryGetArgument(ctx.ArgumentStringTrimmed, "maxSize", out string? maxSizeStr))
        {
            query = query.Substring(0, query.IndexOf("-maxSize", StringComparison.OrdinalIgnoreCase));
        }
        else if (StringHelpers.TryGetArgument(ctx.ArgumentStringTrimmed, "max", out maxSizeStr))
        {
            query = query.Substring(0, query.IndexOf("-max", StringComparison.OrdinalIgnoreCase));
        }

        ctx.Message.AddReactionAsync(Emotes.ThumbsUp).IgnoreExceptions();

        if (SeriesRegex.IsMatch(query))
        {
            await ctx.ReplyAsync($"`{query}` looks like a TV series. Only movies are supported.");
            return;
        }

        // Runs alongside the search and the result filtering below, it's only awaited before we act on the results.
        Task<bool> isKnownSeries = IsKnownSeriesAsync(ctx, query);

        QBittorrentClient.SearchResult[] results;
        try
        {
            await _qBittorrent.LoginAsync(ctx.CancellationToken);

            results = await _qBittorrent.SearchAsync(query, TimeSpan.FromSeconds(6), ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            await ctx.ReplyAsync($"Search for '{query}' failed: {ex.Message}");
            await ctx.DebugAsync(ex);
            return;
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            results = [.. results.Where(r => r.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        if (StringHelpers.TryParseSize(maxSizeStr, 1, out long maxSize))
        {
            results = [.. results.Where(r => r.FileSize <= maxSize)];
        }

        QBittorrentClient.SearchResult[] candidates = FilterOutJunkResults(results);

        if (candidates.Length > 1)
        {
            // Optimistically let the model narrow the list down. If it's unavailable, refuses to answer,
            // or doesn't like anything we found, we just present the programmatically filtered list instead.
            QBittorrentClient.SearchResult[]? relevant = await TryFilterRelevantResultsAsync(ctx, query, candidates);

            if (relevant is { Length: > 0 })
            {
                candidates = relevant;
            }
        }

        if (await isKnownSeries)
        {
            await ctx.ReplyAsync($"`{query}` looks like a TV series. Only movies are supported.");
            return;
        }

        if (candidates.Length == 0)
        {
            await ctx.ReplyAsync("No suitable results found.");
            return;
        }

        if (candidates.Length > MaxOptionsToPresent)
        {
            candidates = [.. candidates.Take(MaxOptionsToPresent)];
        }

        QBittorrentClient.SearchResult? selected = candidates.Length == 1
            ? candidates[0]
            : await PromptForSelectionAsync(ctx, candidates);

        if (selected is null)
        {
            return;
        }

        await DownloadAsync(ctx, new MagnetUri(selected.FileUrl));
    }

    private async Task<QBittorrentClient.SearchResult?> PromptForSelectionAsync(CommandContext ctx, QBittorrentClient.SearchResult[] candidates)
    {
        string id = $"{Command}-{Snowflake.Next()}";

        var menu = new SelectMenuBuilder()
            .WithCustomId(id)
            .WithPlaceholder("Pick the release you want")
            .WithMinValues(1)
            .WithMaxValues(1);

        for (int i = 0; i < candidates.Length; i++)
        {
            QBittorrentClient.SearchResult result = candidates[i];

            string label = string.IsNullOrWhiteSpace(result.FileName) ? $"Result {i + 1}" : result.FileName;

            menu.AddOption(
                label.TruncateWithDotDotDot(100),
                i.ToString(CultureInfo.InvariantCulture),
                $"{result.FileSize.GetRoughSizeString()} - {result.NbSeeders} seeders".TruncateWithDotDotDot(100));
        }

        MessageComponent components = new ComponentBuilder().WithSelectMenu(menu).Build();

        var pending = new PendingSelection(ctx.AuthorId);
        _pendingSelections.TryAdd(id, pending);

        RestUserMessage? message = null;
        try
        {
            message = await ctx.Channel.SendMessageAsync(
                $"{MentionUtils.MentionUser(ctx.AuthorId)} Found {candidates.Length} options, which one do ye want?",
                components: components);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            timeoutCts.CancelAfter(SelectionTimeout);

            int index;
            try
            {
                index = await pending.Selection.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested)
            {
                await ctx.ReplyAsync("Timed out waiting for a selection.");
                return null;
            }

            return (uint)index < (uint)candidates.Length ? candidates[index] : null;
        }
        finally
        {
            _pendingSelections.TryRemove(id, out _);

            if (message is not null)
            {
                message.DeleteAsync().IgnoreExceptions();
            }
        }
    }

    public override async Task HandleMessageComponentAsync(SocketMessageComponent component)
    {
        if (!_pendingSelections.TryGetValue(component.Data.CustomId, out PendingSelection? pending))
        {
            return;
        }

        if (component.User.Id != pending.AuthorId && !Constants.Admins.Contains(component.User.Id))
        {
            await component.RespondAsync("Ye didn't ask for this one.", ephemeral: true);
            return;
        }

        component.DeferAsync().IgnoreExceptions();

        if (int.TryParse(component.Data.Values?.FirstOrDefault(), CultureInfo.InvariantCulture, out int index))
        {
            pending.Selection.TrySetResult(index);
        }
    }

    private async Task DownloadAsync(CommandContext ctx, MagnetUri uri)
    {
        Interlocked.Increment(ref _activeDownloads);

        bool completed = false;
        RestUserMessage? message = null;
        try
        {
            ctx.DebugLog($"Starting download for `{uri.DisplayName}`: <{uri.Url}>");

            await _qBittorrent.LoginAsync(ctx.CancellationToken);
            await _qBittorrent.AddTorrentAsync(uri.Url, "/media/Movies", ctx.CancellationToken);

            // Initial wait for the torrent to start downloading.
            await Task.Delay(3_000, ctx.CancellationToken);

            QBittorrentClient.TorrentInfo info;
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
                        .AddField("Size", info.TotalSize.GetRoughSizeString(), inline: true)
                        .AddField("Progress", $"{progressStr} ({info.TotalDownloaded.GetRoughSizeString()})", inline: true)
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

                await Task.Delay(3000 * _activeDownloads, ctx.CancellationToken);
            }

            completed = true;

            await _jellyfin.RefreshLibraryAsync(ctx.CancellationToken);

            await ctx.ReplyAsync($"Downloaded `{info.Name}` successfully.", mention: true);
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.DebugLog($"Download for `{uri.DisplayName}` was cancelled, aborting the torrent.");
        }
        catch (Exception ex)
        {
            await ctx.ReplyAsync($"Failed to add torrent: {ex.Message}");
            await ctx.DebugAsync(ex);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDownloads);

            if (message is not null)
            {
                message.DeleteAsync().IgnoreExceptions();
            }

            // Never use ctx.CancellationToken here -- if the command was cancelled, the torrent
            // would keep downloading in the background instead of being removed.
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            try
            {
                await _qBittorrent.LoginAsync(cleanupCts.Token);
                await _qBittorrent.DeleteTorrentAsync(uri.Hash, deleteFiles: !completed, cleanupCts.Token);
            }
            catch (Exception ex)
            {
                await ctx.DebugAsync(ex, $"Failed to delete torrent {uri.Hash}");
            }
        }
    }

    /// <summary>
    /// Drops results that can't possibly be what the user wanted (dead torrents, absurd sizes,
    /// TV series, cam rips, ...). Anything that survives this is a plausible option to offer.
    /// </summary>
    private static QBittorrentClient.SearchResult[] FilterOutJunkResults(QBittorrentClient.SearchResult[] results)
    {
        return [.. results
            .Where(r => !string.IsNullOrEmpty(r.FileUrl) && r.FileUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            .Where(r => !string.IsNullOrWhiteSpace(r.FileName))
            .Where(r => r.NbSeeders >= 3)
            .Where(r => r.FileSize is >= MinTorrentSize and <= MaxTorrentSize)
            .Where(r => !SeriesRegex.IsMatch(r.FileName))
            .Where(r => !LowQualityReleaseRegex.IsMatch(r.FileName))
            .Where(r => !HdrRegex.IsMatch(r.FileName))
            .DistinctBy(r => NormalizeName(r.FileName), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(r => r.NbSeeders)
            .Take(MaxCandidatesForAI)];

        static string NormalizeName(string name)
        {
            return string.Create(name.Length, name, static (buffer, name) =>
            {
                int length = 0;
                foreach (char c in name)
                {
                    if (char.IsAsciiLetterOrDigit(c))
                    {
                        buffer[length++] = char.ToLowerInvariant(c);
                    }
                }
                buffer.Slice(length).Fill(' ');
            }).TrimEnd();
        }
    }

    /// <summary>
    /// Checks whether the title the user asked for is a well-known TV show.
    /// Obvious season/episode patterns are matched by <see cref="SeriesRegex"/> instead.
    /// Defaults to false if we can't tell.
    /// </summary>
    private async Task<bool> IsKnownSeriesAsync(CommandContext ctx, string query)
    {
        if (_openAI is null)
        {
            return false;
        }

        try
        {
            IChatClient chatClient = _openAI.GetChat(OpenAIService.DefaultModel);

            string prompt =
                $"""
                Is "{query}" a TV series (a show, or a specific season or episode of one), rather than a movie?

                Respond with true if it names a TV show, or asks for a season/episode of one, or if the title is
                primarily known as a TV show rather than as a movie.

                Respond with false if it names a movie, including movies based on or spun off from a TV series,
                if you don't recognize the title, or if the title is known as both a movie and a TV show.

                When in doubt, respond with false.
                """;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(1));

            ChatResponse<bool> response = await chatClient.GetResponseAsync<bool>(prompt, useJsonSchemaResponseFormat: true, cancellationToken: cts.Token);

            return response.Result;
        }
        catch (Exception ex)
        {
            // Never fail the command over this check -- it may be awaited long after it was started.
            if (!ctx.CancellationToken.IsCancellationRequested)
            {
                ctx.DebugLog($"{nameof(PirateCommand)}: Failed to check if '{query}' is a series: {ex}");
            }

            return false;
        }
    }

    /// <summary>
    /// Asks the model which of the candidates actually match what the user asked for.
    /// Returns null if we couldn't get a usable answer, in which case the caller keeps the unfiltered list.
    /// </summary>
    private async Task<QBittorrentClient.SearchResult[]?> TryFilterRelevantResultsAsync(CommandContext ctx, string query, QBittorrentClient.SearchResult[] candidates)
    {
        if (_openAI is null)
        {
            return null;
        }

        try
        {
            IChatClient chatClient = _openAI.GetChat(OpenAIService.DefaultModel);

            string prompt =
                $"""
                You are part of an automated system that helps a user download a movie they asked for.

                The user asked for: "{query}"

                Below is a numbered list of torrent search results.
                For each result, score from 0 to 1 how likely it is to be the movie the user asked for.

                Score 0 for:
                - A different movie, including sequels, prequels, remakes or "making of" content when the user asked for a specific title.
                - Anything that is a TV series, a season pack or an individual episode. Only movies are supported.
                - Anything that isn't a single movie (collections/packs of several movies, soundtracks, games, software, subtitles or samples).

                Score close to 1 for results that clearly are the requested movie.
                Different releases of the same movie (different resolutions, codecs or release groups) should all score high -
                the user picks between them afterwards, so do not judge the quality of the release.
                Only return results you scored above 0.5.

                Results:
                {string.Join('\n', candidates.Select((r, i) => $"{i + 1}. {r.FileName} ({r.FileSize.GetRoughSizeString()}, {r.NbSeeders} seeders)"))}
                """;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(1));

            ChatResponse<TorrentRelevance[]> response = await chatClient.GetResponseAsync<TorrentRelevance[]>(prompt, useJsonSchemaResponseFormat: true, cancellationToken: cts.Token);

            HashSet<int> relevant = [.. response.Result
                .Where(r => r.Score > 0.5)
                .Select(r => r.Number - 1)
                .Where(i => (uint)i < (uint)candidates.Length)];

            // Preserve our own ordering (most seeders first) instead of trusting the model's.
            return [.. candidates.Where((_, i) => relevant.Contains(i))];
        }
        catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.DebugLog($"{nameof(PirateCommand)}: Failed to filter results for '{query}': {ex}");
            return null;
        }
    }

    private sealed record TorrentRelevance(int Number, double Score);

    private sealed record PendingSelection(ulong AuthorId)
    {
        public TaskCompletionSource<int> Selection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [GeneratedRegex(@"\b(S\d{1,3}[EexX]\d{1,3}|S\d{2,3}|Seasons?[ .]?\d{1,3}|Complete[ .](Series|Season))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesRegex { get; }

    [GeneratedRegex(@"\b(CAM|CAMRIP|HDCAM|HDTS|TELESYNC|TELECINE|SCREENER|DVDSCR|R5|WORKPRINT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LowQualityReleaseRegex { get; }

    [GeneratedRegex(@"\bHDR(10)?(\+|Plus)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex HdrRegex { get; }
}
