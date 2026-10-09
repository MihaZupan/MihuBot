using System.Text.Json;
using System.Globalization;
using MihuBot.Configuration;

namespace MihuBot.YoutubeArchive;

public sealed record YoutubeArchiveMetadata(string Title, bool IsMusic, bool IsLive);

public interface IYoutubeArchiveDownloader
{
    Task<YoutubeArchiveMetadata> GetMetadataAsync(string videoId, CancellationToken cancellationToken, YoutubeArchiveCookie[] cookies = null);
    Task<string> DownloadAsync(YoutubeArchiveJob job, string directory, CancellationToken cancellationToken, YoutubeArchiveCookie[] cookies = null);
}

public sealed class YoutubeArchiveDownloader : IYoutubeArchiveDownloader
{
    public const string CookiesFileKey = "YoutubeArchive.CookiesFile";
    private readonly IConfigurationService _configuration;
    private readonly Func<string[], CancellationToken, Task<string>> _run;

    public YoutubeArchiveDownloader(IConfigurationService configuration)
        : this(configuration, RunAsync)
    {
    }

    internal YoutubeArchiveDownloader(IConfigurationService configuration, Func<string[], CancellationToken, Task<string>> run)
    {
        _configuration = configuration;
        _run = run;
    }

    internal string[] GetArguments(string videoId, IEnumerable<string> arguments, string cookiesFile = null)
    {
        List<string> result = ["--ignore-config", "--no-playlist", "--no-progress", "--socket-timeout", "30", "--retries", "3"];

        if (cookiesFile is null)
        {
            _configuration.TryGet(null, CookiesFileKey, out cookiesFile);
        }

        if (!string.IsNullOrWhiteSpace(cookiesFile))
        {
            result.AddRange(["--cookies", cookiesFile]);
        }

        result.AddRange(arguments);

        if (!string.IsNullOrWhiteSpace(cookiesFile))
        {
            result.Add("--no-write-info-json");
        }

        result.AddRange(["--", YoutubeArchiveValidation.GetUrl(videoId)]);
        return [.. result];
    }

    public async Task<YoutubeArchiveMetadata> GetMetadataAsync(string videoId, CancellationToken cancellationToken, YoutubeArchiveCookie[] cookies = null)
    {
        string json = await RunWithCookiesAsync(videoId, ["--dump-single-json", "--skip-download"], cookies, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        bool music = root.TryGetProperty("categories", out var categories) &&
            categories.ValueKind == JsonValueKind.Array &&
            categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "Music");
        bool live = root.TryGetProperty("live_status", out var liveStatus) &&
            liveStatus.GetString() is "is_live" or "is_upcoming" or "post_live";
        return new YoutubeArchiveMetadata(root.GetProperty("title").GetString(), music, live);
    }

    internal static string[] GetDownloadArguments(YoutubeArchiveJob job, string directory)
    {
        List<string> arguments =
        [
            "--no-simulate", "--quiet", "--windows-filenames",
            "--match-filter", "!is_live & !is_upcoming & live_status!=post_live",
            "--write-info-json", "--write-thumbnail", "--convert-thumbnails", "jpg",
            "--embed-metadata", "--no-embed-info-json", "--output", Path.Combine(directory, "%(title).150B [%(id)s].%(ext)s"),
            "--print", "after_move:filepath",
        ];

        if (job.Mode == "audio")
        {
            arguments.AddRange(["--format", "bestaudio", "--extract-audio", "--audio-format", "best"]);
        }
        else
        {
            string filter = job.MaxHeight is int height ? $"[height<={height}]" : "";
            arguments.AddRange(["--format", $"bv{filter}+ba/b{filter}", "--merge-output-format", "mkv", "--remux-video", "mkv"]);
        }

        return [.. arguments];
    }

    public async Task<string> DownloadAsync(YoutubeArchiveJob job, string directory, CancellationToken cancellationToken, YoutubeArchiveCookie[] cookies = null)
    {
        string output = await RunWithCookiesAsync(job.VideoId, GetDownloadArguments(job, directory), cookies, cancellationToken);
        string path = output.Trim();
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;

        if (path.Length == 0 ||
            !Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !System.IO.File.Exists(path))
        {
            throw new InvalidOperationException("yt-dlp did not produce a media file. Live/upcoming videos cannot be archived.");
        }

        return path;
    }

    private async Task<string> RunWithCookiesAsync(string videoId, string[] arguments, YoutubeArchiveCookie[] cookies, CancellationToken cancellationToken)
    {
        string temporary = cookies is not null ? await CreateCookiesFileAsync(cookies, cancellationToken) : null;

        try
        {
            return await _run(GetArguments(videoId, arguments, temporary), cancellationToken);
        }
        finally
        {
            if (temporary is not null)
            {
                System.IO.File.Delete(temporary);
            }
        }
    }

    internal static async Task<string> CreateCookiesFileAsync(YoutubeArchiveCookie[] cookies, CancellationToken cancellationToken)
    {
        if (!YoutubeArchiveValidation.AreCookiesValid(cookies))
        {
            throw new ArgumentException("Invalid YouTube cookies.", nameof(cookies));
        }

        string path = Path.Combine(Path.GetTempPath(), $"mihubot-youtube-cookies-{Guid.NewGuid():N}.txt");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using var stream = new FileStream(path, options);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync("# Netscape HTTP Cookie File");

            foreach (YoutubeArchiveCookie cookie in cookies)
            {
                string domain = (cookie.HttpOnly ? "#HttpOnly_" : "") + cookie.Domain;
                string line = string.Join('\t', domain, cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE",
                    cookie.Path, cookie.Secure ? "TRUE" : "FALSE", (cookie.Expires ?? 0).ToString(CultureInfo.InvariantCulture), cookie.Name, cookie.Value);
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }

            return path;
        }
        catch
        {
            System.IO.File.Delete(path);
            throw;
        }
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("yt-dlp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            }
        };
        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> output = ReadBoundedAsync(process.StandardOutput, 8 * 1024 * 1024);
        Task<string> error = ReadBoundedAsync(process.StandardError, 16 * 1024);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp exited with code {process.ExitCode}: {await error}");
            }

            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await Task.WhenAll(output, error);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int read;

        while ((read = await reader.ReadAsync(buffer)) != 0)
        {
            result.Append(buffer, 0, read);

            if (result.Length > limit)
            {
                result.Remove(0, result.Length - limit);
            }
        }

        return result.ToString();
    }
}
