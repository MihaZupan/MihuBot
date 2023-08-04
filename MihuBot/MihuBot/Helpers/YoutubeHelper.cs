﻿using Google.Apis.YouTube.v3;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

#nullable enable

namespace MihuBot.Helpers;

internal static class YoutubeHelper
{
    public static bool TryParsePlaylistId(string playlistUrl, [NotNullWhen(true)] out string? playlistId)
    {
        playlistId = null;

        // https://www.youtube.com/playlist?list=PLOU2XLYxmsIJGErt5rrCqaSGTMyyqNt2H
        string regularMatch = Regex.Match(playlistUrl, @"youtube\..+?/playlist.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
        if (ValidatePlaylistId(regularMatch))
        {
            playlistId = regularMatch;
            return true;
        }

        // https://www.youtube.com/watch?v=b8m9zhNAgKs&list=PL9tY0BWXOZFuFEG_GtOBZ8-8wbkH-NVAr
        string compositeMatch = Regex.Match(playlistUrl, @"youtube\..+?/watch.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
        if (ValidatePlaylistId(compositeMatch))
        {
            playlistId = compositeMatch;
            return true;
        }

        // https://youtu.be/b8m9zhNAgKs/?list=PL9tY0BWXOZFuFEG_GtOBZ8-8wbkH-NVAr
        string shortCompositeMatch = Regex.Match(playlistUrl, @"youtu\.be/.*?/.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
        if (ValidatePlaylistId(shortCompositeMatch))
        {
            playlistId = shortCompositeMatch;
            return true;
        }

        // https://www.youtube.com/embed/b8m9zhNAgKs/?list=PL9tY0BWXOZFuFEG_GtOBZ8-8wbkH-NVAr
        string embedCompositeMatch = Regex.Match(playlistUrl, @"youtube\..+?/embed/.*?/.*?list=(.*?)(?:&|/|$)").Groups[1].Value;
        if (ValidatePlaylistId(embedCompositeMatch))
        {
            playlistId = embedCompositeMatch;
            return true;
        }

        return false;
    }
    private static bool ValidatePlaylistId(string playlistId)
    {
        if (playlistId is null)
            return false;

        if (playlistId.Length != 2 &&
            playlistId.Length != 13 &&
            playlistId.Length != 18 &&
            playlistId.Length != 24 &&
            playlistId.Length != 34)
            return false;

        return !Regex.IsMatch(playlistId, @"[^0-9a-zA-Z_\-]");
    }

    public static bool TryParseVideoId(string videoUrl, [NotNullWhen(true)] out string? videoId)
    {
        videoId = null;

        // https://www.youtube.com/watch?v=yIVRs6YSbOM
        string regularMatch = Regex.Match(videoUrl, @"youtube\..+?/watch.*?v=(.*?)(?:&|/|$)").Groups[1].Value;
        if (ValidateVideoId(regularMatch))
        {
            videoId = regularMatch;
            return true;
        }

        // https://youtu.be/yIVRs6YSbOM
        string shortMatch = Regex.Match(videoUrl, @"youtu\.be/(.*?)(?:\?|&|/|$)").Groups[1].Value;
        if (ValidateVideoId(shortMatch))
        {
            videoId = shortMatch;
            return true;
        }

        // https://www.youtube.com/embed/yIVRs6YSbOM
        string embedMatch = Regex.Match(videoUrl, @"youtube\..+?/embed/(.*?)(?:\?|&|/|$)").Groups[1].Value;
        if (ValidateVideoId(embedMatch))
        {
            videoId = embedMatch;
            return true;
        }

        return false;

        static bool ValidateVideoId(string videoId)
        {
            if (videoId is null || videoId.Length != 11)
                return false;

            return !Regex.IsMatch(videoId, @"[^0-9a-zA-Z_\-]");
        }
    }


    public static readonly YoutubeClient Youtube = new();
    public static readonly StreamClient Streams = Youtube.Videos.Streams;

    public static async Task<List<IVideo>> GetVideosAsync(string playlistId, YouTubeService? youtubeService = null)
    {
        if (youtubeService is not null)
        {
            List<IVideo> videos = new();

            string pageToken = "";
            while (pageToken is not null)
            {
                PlaylistItemsResource.ListRequest playlistItemsRequest = youtubeService.PlaylistItems.List("snippet");
                playlistItemsRequest.Id = playlistId;
                playlistItemsRequest.PageToken = pageToken;

                var response = await playlistItemsRequest.ExecuteAsync();
                pageToken = playlistItemsRequest.PageToken;

                foreach (var item in response.Items)
                {
                    videos.Add(Transform(item));
                }
            }

            return videos;
        }
        else
        {
            return await Youtube.Playlists.GetVideosAsync(playlistId).Select(v => (IVideo)v).ToListAsync();
        }
    }

    public static async Task SendVideoAsync(string id, ISocketMessageChannel channel, bool useOpus)
    {
        try
        {
            var video = await Youtube.Videos.GetAsync(id);

            if (video.Duration > TimeSpan.FromMinutes(65))
            {
                return;
            }

            var bestAudio = GetBestAudio(await Streams.GetManifestAsync(id), out string extension);

            string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + extension);

            await Streams.DownloadAsync(bestAudio, filePath);

            string outputExtension = useOpus ? ".opus" : ".mp3";
            string outFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + outputExtension);

            try
            {
                await ConvertToAudioOutputAsync(filePath, outFilePath, bitrate: 128);
                using FileStream fs = File.OpenRead(outFilePath);
                await channel.SendFileAsync(fs, GetFileName(video.Title, outputExtension));
            }
            finally
            {
                File.Delete(filePath);
                File.Delete(outFilePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public static async Task SendPlaylistAsync(string id, ISocketMessageChannel channel, bool useOpus, YouTubeService? youtubeService = null)
    {
        try
        {
            List<IVideo> videos = await GetVideosAsync(id, youtubeService);

            Console.WriteLine("Processing playlist with " + videos.Count + " items");

            Task[] tasks = new Task[Math.Min(videos.Count, 2)];
            int index = -1;
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    while (true)
                    {
                        int local = Interlocked.Increment(ref index);
                        if (local >= videos.Count)
                            return;

                        await SendVideoAsync(videos[local].Id, channel, useOpus);
                    }
                });
            }
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public static async Task ConvertToAudioOutputAsync(string sourcePath, string targetPath, int bitrate, CancellationToken cancellationToken = default)
    {
        await FFMpegConvertAsync(sourcePath, targetPath, $"-b:a {bitrate}k -vn", cancellationToken);
    }

    public static async Task FFMpegConvertAsync(string sourcePath, string targetPath, string arguments, CancellationToken cancellationToken = default)
    {
        using Process ffmpeg = new()
        {
            StartInfo = new ProcessStartInfo("ffmpeg")
            {
                Arguments = $"-y -hide_banner -loglevel warning -i \"{sourcePath}\" {arguments} \"{targetPath}\"",
                UseShellExecute = false
            }
        };

        try
        {
            ffmpeg.Start();
            await ffmpeg.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                ffmpeg.Kill();
            }
            catch { }
        }
    }

    public static string GetFileName(string title, string extension)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            title = title.Replace(c, ' ');

        foreach (var c in Path.GetInvalidPathChars())
            title = title.Replace(c, ' ');

        title = title.Replace('.', ' ');

        while (title.Contains("  "))
            title = title.Replace("  ", " ");

        title = title.Replace('"', '\'');

        return title + extension;
    }

    public static IStreamInfo GetBestAudio(StreamManifest manifest, out string extension)
    {
        IStreamInfo audioOnly = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        if (audioOnly != null)
        {
            extension = "." + audioOnly.Container.Name;
            return audioOnly;
        }

        IStreamInfo bestAudio = manifest.GetMuxedStreams().TryGetWithHighestBitrate()
            ?? throw new Exception("TryGetWithHighestBitrate returned null?");

        extension = bestAudio.Container.Name == "aac" ? ".aac" : ".vorbis";
        return bestAudio;
    }

    public static async Task<IVideo?> TrySearchAsync(string query, YouTubeService? youtubeService = null)
    {
        try
        {
            if (youtubeService is not null)
            {
                SearchResource.ListRequest searchListRequest = youtubeService.Search.List("snippet");
                searchListRequest.Q = query;
                searchListRequest.MaxResults = 5;
                searchListRequest.Type = "video";

                var searchListResponse = await searchListRequest.ExecuteAsync();

                if (searchListResponse.Items.Count != 0)
                {
                    Google.Apis.YouTube.v3.Data.SearchResult bestMatch =
                        searchListResponse.Items.FirstOrDefault(i => i.Snippet.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ??
                        searchListResponse.Items.First();

                    return Transform(bestMatch);
                }
            }
            else
            {
                VideoSearchResult[] results = await Youtube.Search.GetVideosAsync(query).Take(5).ToArrayAsync();

                if (results.Length != 0)
                {
                    VideoSearchResult? titleMatch = results.FirstOrDefault(r => r.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
                    if (titleMatch is not null)
                    {
                        return titleMatch;
                    }

                    return results[0];
                }
            }
        }
        catch { }

        return null;
    }

    public static Task<IVideo?> TryFindSongAsync(string title, string artist, YouTubeService? youtubeService = null)
    {
        return TrySearchAsync($"{artist} {title}", youtubeService);
    }

    public static ValueTask<Video> GetVideoAsync(VideoId videoId, CancellationToken cancellationToken = default)
    {
        return Youtube.Videos.GetAsync(videoId, cancellationToken);
    }

    private static IVideo Transform(Google.Apis.YouTube.v3.Data.SearchResult searchResult)
    {
        var snippet = searchResult.Snippet;
        var thumbnail = snippet.Thumbnails.Maxres ?? snippet.Thumbnails.High ?? snippet.Thumbnails.Standard ?? snippet.Thumbnails.Default__;

        return new Video(
            searchResult.Id.VideoId,
            snippet.Title,
            new Author(snippet.ChannelId, snippet.ChannelTitle),
            snippet.PublishedAtDateTimeOffset ?? new DateTime(3000, 1, 1),
            snippet.Description,
            duration: null,
            thumbnails: new Thumbnail[] {
                new Thumbnail(thumbnail.Url, new Resolution((int)(thumbnail.Width ?? 1), (int)(thumbnail.Height ?? 1)))
            },
            keywords: Array.Empty<string>(),
            engagement: new Engagement(0, 0, 0));
    }

    private static IVideo Transform(Google.Apis.YouTube.v3.Data.PlaylistItem playlistItem)
    {
        var snippet = playlistItem.Snippet;
        var thumbnail = snippet.Thumbnails.Maxres ?? snippet.Thumbnails.High ?? snippet.Thumbnails.Standard ?? snippet.Thumbnails.Default__;

        return new Video(
            playlistItem.Id,
            snippet.Title,
            new Author(snippet.ChannelId, snippet.ChannelTitle),
            snippet.PublishedAtDateTimeOffset ?? new DateTime(3000, 1, 1),
            snippet.Description,
            duration: null,
            thumbnails: new Thumbnail[] {
                new Thumbnail(thumbnail.Url, new Resolution((int)(thumbnail.Width ?? 1), (int)(thumbnail.Height ?? 1)))
            },
            keywords: Array.Empty<string>(),
            engagement: new Engagement(0, 0, 0));
    }

    public static TimeSpan Duration(this IStreamInfo streamInfo)
    {
        long bytesPerSecond = Math.Min(1, streamInfo.Bitrate.BitsPerSecond / 8);
        return TimeSpan.FromSeconds(streamInfo.Size.Bytes / bytesPerSecond);
    }
}
