﻿using Azure.Storage.Blobs;
using Discord.Rest;
using MihuBot.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class VodsCommand : CommandBase
    {
        public override string Command => "vods";
        public override string[] Aliases => new[] { "vod" };

        private readonly BlobContainerClient BlobContainerClient =
            new BlobContainerClient(Secrets.AzureStorage.ConnectionString, Secrets.AzureStorage.VodsContainerName);

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.Arguments.Any())
            {
                await ctx.ReplyAsync("Usage: `!vods vodLink [format_id]`");
                return;
            }

            Match match = Regex.Match(ctx.Arguments[0], @"https:\/\/www\.twitch\.tv\/(?:videos|.*?\/clip)\/[^\/\?\#]+", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                await ctx.ReplyAsync("Unknown vod link format");
                return;
            }

            string link = match.Value;

            YoutubeDlMetadata metadata;
            try
            {
                string youtubeDlJson = await RunProcessAndReadOutputAsync("youtube-dl", $"-j {link}");
                try
                {
                    metadata = JsonConvert.DeserializeObject<YoutubeDlMetadata>(youtubeDlJson);
                }
                catch (Exception ex) { throw new Exception("Json: " + youtubeDlJson, ex); }
            }
            catch (Exception ex)
            {
                await ctx.DebugAsync($"{ex} for {link}");
                await ctx.ReplyAsync($"Failed to fetch vod metadata");
                return;
            }

            if (metadata.IsLive)
            {
                await ctx.ReplyAsync($"Please queue the download after the stream has ended");
                return;
            }

            if (metadata.Formats is null || metadata.Formats.Length == 0)
            {
                await ctx.ReplyAsync($"Failed to load any media formats");
                return;
            }

            YoutubeDlFormat selectedFormat = metadata.Formats.OrderByDescending(f => f).First();

            if (ctx.Arguments.Length > 1)
            {
                string formatId = ctx.Arguments[1];
                try
                {
                    selectedFormat = metadata.Formats.Single(f => f.FormatId.Equals(formatId, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    await ctx.ReplyAsync($"Failed to match {formatId} against [ {string.Join(", ", metadata.Formats.Select(f => f.FormatId))} ]");
                    return;
                }
            }

            HttpResponseMessage response = null;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, selectedFormat.Url);
                foreach (var header in selectedFormat.HttpHeaders)
                {
                    request.Headers.Add(header.Key, header.Value);
                }

                response = await ctx.Services.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                response?.Dispose();
                await ctx.DebugAsync(ex.ToString());
                await ctx.ReplyAsync($"Failed to initiate a media transfer");
                return;
            }

            try
            {
                using var responseStream = await response.Content.ReadAsStreamAsync();

                string fileName = $"{Path.GetFileNameWithoutExtension(metadata.Filename)}.{selectedFormat.Ext}";
                string blobName = $"{metadata.UploaderId ?? $"unknown_{metadata.Id}"}/{DateTime.UtcNow.ToISODateTime()}_{fileName}";
                BlobClient blobClient = BlobContainerClient.GetBlobClient(blobName);

                Task<RestUserMessage> statusMessage = ctx.ReplyAsync($"Saving *{metadata.Title}* ({metadata.Duration} s) ...");
                try
                {
                    await blobClient.UploadAsync(responseStream);
                    await ctx.ReplyAsync($"Uploaded *{metadata.Title}* to\n{blobClient.Uri.AbsoluteUri}");
                }
                finally
                {
                    await (await statusMessage).DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                await ctx.DebugAsync(ex.ToString());
                await ctx.ReplyAsync($"Failed to initiate a media transfer");
                return;
            }
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy), ItemNullValueHandling = NullValueHandling.Ignore)]
        private class YoutubeDlMetadata
        {
            public string Id;
            public string Title;
            public string UploaderId;
            public bool IsLive;
            public int Duration;

            public YoutubeDlFormat[] Formats;

            [JsonProperty(PropertyName = "_filename")]
            public string Filename;
        }

        private class YoutubeDlFormat : IComparable<YoutubeDlFormat>, IComparable
        {
            public string FormatId;
            public string Ext;
            public string Url;
            public int? Tbr;
            public int? Height;
            public Dictionary<string, string> HttpHeaders;

            public int CompareTo(object obj) => CompareTo(obj as YoutubeDlFormat);

            public int CompareTo([AllowNull] YoutubeDlFormat other)
            {
                if (other is null) return 1;

                if (Tbr.HasValue && other.Tbr.HasValue)
                    return Tbr.Value.CompareTo(other.Tbr.Value);

                if (Height.HasValue && other.Height.HasValue)
                    return Height.Value.CompareTo(other.Height.Value);

                return FormatId.CompareTo(other.FormatId);
            }
        }

        private static async Task<string> RunProcessAndReadOutputAsync(string name, string arguments)
        {
            using var proc = new Process();
            proc.StartInfo.FileName = name;
            proc.StartInfo.Arguments = arguments;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;

            proc.Start();

            string output = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit();
            return output;
        }
    }
}