using Newtonsoft.Json;

namespace MihuBot.Helpers
{
    public static class YoutubeDl
    {
        public static async Task<T> GetJsonAsync<T>(string url)
        {
            using var proc = new Process();
            proc.StartInfo.FileName = "youtube-dl";
            proc.StartInfo.Arguments = $"-j \"{url}\"";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;

            proc.Start();

            Task<string> outputTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            await Task.WhenAll(outputTask, errorTask);

            string error = await errorTask;
            if (!string.IsNullOrEmpty(error))
            {
                throw new Exception($"YoutubeDl failed for \"{url}\" with \"{error}\"");
            }

            string output = await outputTask;
            return JsonConvert.DeserializeObject<T>(output);
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
    }
}
