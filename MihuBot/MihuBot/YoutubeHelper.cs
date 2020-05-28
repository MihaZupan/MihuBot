using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace YeswBot
{
    static class YoutubeHelper
    {
        public static bool TryParsePlaylistId(string playlistUrl, out string playlistId)
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

        public static bool TryParseVideoId(string videoUrl, out string videoId)
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


        private static readonly YoutubeClient Youtube = new YoutubeClient();

        public static async Task SendVideoAsync(string id, ISocketMessageChannel channel)
        {
            try
            {
                var video = await Youtube.Videos.GetAsync(id);

                if (video.Duration > TimeSpan.FromMinutes(65))
                {
                    return;
                }

                Console.WriteLine("Processing " + video.Title);

                var bestAudio = GetBestAudio(await Youtube.Videos.Streams.GetManifestAsync(id), out string extension);

                string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + extension);

                await Youtube.Videos.Streams.DownloadAsync(bestAudio, filePath);

                string mp3FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".mp3");

                try
                {
                    ConvertToMp3(filePath, mp3FilePath);
                    using FileStream fs = File.OpenRead(mp3FilePath);
                    await channel.SendFileAsync(fs, GetFileName(video.Title));
                }
                finally
                {
                    File.Delete(filePath);
                    File.Delete(mp3FilePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public static async Task SendPlaylistAsync(string id, ISocketMessageChannel channel)
        {
            try
            {
                List<Video> videos = new List<Video>();
                await foreach (var video in Youtube.Playlists.GetVideosAsync(id))
                {
                    videos.Add(video);
                }

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

                            await SendVideoAsync(videos[local].Id, channel);
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

        private static void ConvertToMp3(string sourcePath, string targetPath)
        {
            using Process ffmpeg = new Process();
            ffmpeg.StartInfo.FileName = @"ffmpeg";
            ffmpeg.StartInfo.Arguments = $"-i \"{sourcePath}\" -b:a 192k -vn \"{targetPath}\"";
            ffmpeg.Start();
            ffmpeg.WaitForExit();
        }

        private static string GetFileName(string title)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                title = title.Replace(c, ' ');

            foreach (var c in Path.GetInvalidPathChars())
                title = title.Replace(c, ' ');

            title = title.Replace('.', ' ');

            while (title.Contains("  "))
                title = title.Replace("  ", " ");

            return title + ".mp3";
        }

        private static IStreamInfo GetBestAudio(StreamManifest manifest, out string extension)
        {
            var audioOnly = manifest.GetAudioOnly().WithHighestBitrate();

            if (audioOnly != null)
            {
                extension = "." + audioOnly.Container.Name;
                return audioOnly;
            }

            var bestAudio = manifest.GetMuxed().OrderByDescending(a => a.Bitrate).FirstOrDefault();

            extension = bestAudio.Container.Name == "aac" ? ".aac" : ".vorbis";
            return bestAudio;
        }
    }
}
