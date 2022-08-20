using Azure.Storage.Blobs;
using Discord.Rest;
using System.Text.RegularExpressions;

namespace MihuBot.Commands;

public sealed class VodsCommand : CommandBase
{
    public override string Command => "vods";
    public override string[] Aliases => new[] { "vod" };

    private readonly BlobContainerClient BlobContainerClient;

    public VodsCommand(IConfiguration configuration)
    {
        if (Program.AzureEnabled)
        {
            BlobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString"],
                "vods");
        }
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!Program.AzureEnabled)
            return;

        if (!await ctx.RequirePermissionAsync("vods"))
            return;

        if (!ctx.Arguments.Any())
        {
            await ctx.ReplyAsync("Usage: `!vods vodLink [format_id]`");
            return;
        }

        Match match = Regex.Match(ctx.Arguments[0], @"https:\/\/www\.twitch\.tv\/(?:videos?|.*?\/clip)\/[^\/\?\#]+", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            await ctx.ReplyAsync("Unknown vod link format");
            return;
        }

        string link = match.Value;

        YoutubeDl.YoutubeDlMetadata metadata;
        try
        {
            metadata = await YoutubeDl.GetMetadataAsync(link);
        }
        catch (Exception ex)
        {
            await ctx.DebugAsync(ex, $"Link {link}");
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

        YoutubeDl.YoutubeDlFormat selectedFormat = metadata.Formats.OrderByDescending(f => f).First();

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

        try
        {
            var argumentBuilder = new StringBuilder();

            if (selectedFormat.HttpHeaders != null)
            {
                YoutubeDl.SerializeHeadersForCmd(argumentBuilder, selectedFormat.HttpHeaders);
            }

            argumentBuilder.Append("-i \"").Append(selectedFormat.Url).Append("\" ");

            if (selectedFormat.Url.EndsWith("m3u8", StringComparison.OrdinalIgnoreCase))
                argumentBuilder.Append("-c:v copy -c:a libopus -b:a 160k");
            else
                argumentBuilder.Append("-c copy");

            argumentBuilder.Append(" -f matroska -");

            string fileName = $"{Path.GetFileNameWithoutExtension(metadata.Filename)}.mkv";
            string blobName = $"{DateTime.UtcNow.ToISODateTime()}_{fileName}";
            BlobClient blobClient = BlobContainerClient.GetBlobClient(blobName);

            Task<RestUserMessage> statusMessage = metadata.Duration < 30
                ? null
                : ctx.ReplyAsync($"Saving *{metadata.Title}* ({(int)metadata.Duration} s) ...");

            try
            {
                using var proc = new Process();
                proc.StartInfo.FileName = "ffmpeg";
                proc.StartInfo.Arguments = argumentBuilder.ToString();
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardInput = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;

                proc.Start();

                Task<string> errorReader = proc.StandardError.ReadToEndAsync();

                try
                {
                    await blobClient.UploadAsync(proc.StandardOutput.BaseStream);
                }
                catch (Exception ex)
                {
                    string error = await errorReader.TimeoutAfter(TimeSpan.FromSeconds(5));
                    throw new Exception(error, ex);
                }

                ctx.DebugLog(await errorReader);

                await ctx.ReplyAsync($"Uploaded *{metadata.Title}* to\n<{blobClient.Uri.AbsoluteUri}>");
            }
            finally
            {
                if (statusMessage != null)
                    await (await statusMessage).DeleteAsync();
            }
        }
        catch (Exception ex)
        {
            await ctx.DebugAsync(ex);
            await ctx.ReplyAsync($"Failed to initiate a media transfer");
            return;
        }
    }
}
