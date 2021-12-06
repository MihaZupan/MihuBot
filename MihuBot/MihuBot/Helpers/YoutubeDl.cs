using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace MihuBot.Helpers
{
    public static class YoutubeDl
    {
        public static async Task<YoutubeDlMetadata> GetMetadataAsync(string url)
        {
            using var proc = new Process();
            proc.StartInfo.FileName = "yt-dlp";
            proc.StartInfo.Arguments = $"-j \"{url}\"";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;

            proc.Start();

            Task<string> outputTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync();

            await Task.WhenAll(outputTask, errorTask);

            string error = await errorTask;
            if (!string.IsNullOrEmpty(error))
            {
                throw new Exception($"YoutubeDl failed for \"{url}\" with \"{error}\"");
            }

            string output = await outputTask;
            return JsonConvert.DeserializeObject<YoutubeDlMetadata>(output);
        }

        public static void SerializeHeadersForCmd(StringBuilder sb, Dictionary<string, string> headers)
        {
            foreach (var header in headers)
            {
                sb
                    .Append("-headers \"")
                    .Append(header.Key)
                    .Append(": ")
                    .Append(header.Value)
                    .Append("\" ");
            }
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy), ItemNullValueHandling = NullValueHandling.Ignore)]
        public class YoutubeDlMetadata
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public bool IsLive { get; set; }
            public double Duration { get; set; }

            public string Url { get; set; }
            public Dictionary<string, string> HttpHeaders { get; set; }

            public YoutubeDlFormat[] Formats { get; set; }

            [JsonProperty(PropertyName = "_filename")]
            public string Filename { get; set; }

            public YoutubeDlFormat GetBestAudio()
            {
                var withAudio = Formats
                    .Where(f => f.Acodec != "none")
                    .ToArray();

                if (withAudio.Length == 0)
                {
                    return null;
                }

                var audioOnly = withAudio
                    .Where(f => f.Vcodec == "none")
                    .ToArray();

                if (audioOnly.Length != 0)
                {
                    var opus = audioOnly
                        .Where(f => f.Acodec == "opus")
                        .ToArray();

                    if (opus.Length != 0)
                    {
                        return
                            audioOnly.Where(f => f.Abr.HasValue).MaxBy(f => f.Abr) ??
                            audioOnly.First();
                    }

                    return
                        audioOnly.Where(f => f.Abr.HasValue).MaxBy(f => f.Abr) ??
                        audioOnly.Where(f => f.Tbr.HasValue).MaxBy(f => f.Tbr) ??
                        audioOnly.First();
                }

                return
                    withAudio.Where(f => f.Abr.HasValue).MaxBy(f => f.Abr) ??
                    withAudio.Where(f => f.Tbr.HasValue).MaxBy(f => f.Tbr) ??
                    withAudio.First();
            }
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy), ItemNullValueHandling = NullValueHandling.Ignore)]
        public class YoutubeDlFormat : IComparable<YoutubeDlFormat>, IComparable
        {
            public string FormatId { get; set; }
            public string Format { get; set; }
            public string Acodec { get; set; }
            public string Vcodec { get; set; }
            public string Url { get; set; }
            public double? Tbr { get; set; }
            public double? Abr { get; set; }
            public double? Height { get; set; }
            public Dictionary<string, string> HttpHeaders { get; set; }

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
    }
}
