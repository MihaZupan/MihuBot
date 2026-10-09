using System.Threading.Channels;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MihuBot.YoutubeArchive;

public sealed class YoutubeArchiveService : BackgroundService
{
    private readonly SynchronizedLocalJsonStore<YoutubeArchiveState> _store;
    private readonly IYoutubeArchiveDownloader _downloader;
    private readonly Action<string> _log;
    private readonly string _directory;
    private readonly IDataProtector _cookiesProtector;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _metadataLock = new(1, 1);

    public YoutubeArchiveService(IConfiguration configuration, IYoutubeArchiveDownloader downloader, Logger logger)
        : this(configuration["YoutubeArchive:Directory"], "YoutubeArchive.json", downloader,
            message => logger.DebugLog(message))
    {
    }

    internal YoutubeArchiveService(string directory, string statePath, IYoutubeArchiveDownloader downloader,
        Action<string> log)
    {
        _directory = Path.GetFullPath(directory);
        _downloader = downloader;
        _log = log;
        string fullStatePath = Path.GetFullPath(Path.IsPathRooted(statePath) ? statePath : Path.Combine(Constants.StateDirectory, statePath));
        _store = new(fullStatePath);
        _cookiesProtector = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetDirectoryName(fullStatePath), "YoutubeArchiveKeys")),
            options => options.SetApplicationName("MihuBot.YoutubeArchive")).CreateProtector("BrowserCookies.v1");
    }

    public YoutubeArchiveJob[] GetJobs(int limit = int.MaxValue) =>
        _store.Query(state => state.Jobs.OrderByDescending(j => j.CreatedAt).Take(limit).ToArray());

    public YoutubeArchiveJob GetJob(Guid id) => _store.Query(state => state.Jobs.FirstOrDefault(j => j.Id == id));

    public YoutubeArchiveDashboard GetDashboard() => _store.Query(state =>
    {
        YoutubeArchiveJob[] active = [.. state.Jobs.Where(j => j.Status is "queued" or "running").OrderBy(j => j.CreatedAt)];
        YoutubeArchiveJob[] recent = [.. state.Jobs.Where(j => j.Status is not ("queued" or "running"))
            .OrderByDescending(j => j.CreatedAt).Take(50)];
        return new YoutubeArchiveDashboard(
            active.Count(j => j.Status == "queued"),
            active.Count(j => j.Status == "running"),
            state.Jobs.Count(j => j.Status == "completed"),
            state.Jobs.Count(j => j.Status == "failed"),
            [.. active.OrderByDescending(j => j.Status == "running"), .. recent]);
    });

    public YoutubeArchiveJob Enqueue(YoutubeArchiveRequest request)
    {
        if (!YoutubeArchiveValidation.IsValid(request))
        {
            throw new ArgumentException("Invalid YouTube archive request.", nameof(request));
        }

        YoutubeHelper.TryParseVideoUrl(request.Url, out string videoId);
        YoutubeArchiveJob job = null;
        _store.Modify(state =>
        {
            job = state.Jobs.FirstOrDefault(j => j.VideoId == videoId && j.Mode == request.Mode &&
                j.MaxHeight == request.MaxHeight && j.Status is "queued" or "running" or "completed");

            if (job is not null)
            {
                if (job.Status == "completed" &&
                    (string.IsNullOrEmpty(job.File) || !System.IO.File.Exists(Path.Combine(_directory, job.File))))
                {
                    throw new FileNotFoundException("Previously archived media is missing. Restore the archive mount or remove the job's history entry while the bot is stopped.", job.File);
                }

                return;
            }

            if (state.Jobs.Count(j => j.Status is "queued" or "running") >= 100)
            {
                throw new InvalidOperationException("The archive queue is full (100 pending jobs).");
            }

            job = new YoutubeArchiveJob
            {
                VideoId = videoId,
                Mode = request.Mode,
                MaxHeight = request.MaxHeight,
                ProtectedCookies = request.Cookies is not null ? _cookiesProtector.Protect(JsonSerializer.Serialize(request.Cookies)) : null
            };
            state.Jobs.Add(job);
        });
        _wake.Writer.TryWrite(true);
        return job;
    }

    public async Task<YoutubeArchiveMetadata> GetMetadataAsync(string videoId, CancellationToken cancellationToken, YoutubeArchiveCookie[] cookies = null)
    {
        if (!await _metadataLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("Another video lookup is in progress. Try again shortly.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            return await _downloader.GetMetadataAsync(videoId, timeout.Token, cookies);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"YouTube metadata lookup for {videoId} failed: {ex}");
            throw;
        }
        finally
        {
            _metadataLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_directory);
        _store.Modify(state =>
        {
            for (int i = 0; i < state.Jobs.Count; i++)
            {
                if (state.Jobs[i].Status == "running")
                {
                    state.Jobs[i] = state.Jobs[i] with { Status = "queued" };
                }
            }
        });
        _wake.Writer.TryWrite(true);

        while (await _wake.Reader.WaitToReadAsync(stoppingToken))
        {
            _wake.Reader.TryRead(out _);
            YoutubeArchiveJob job;

            while ((job = _store.Query(state => state.Jobs.FirstOrDefault(j => j.Status == "queued"))) is not null)
            {
                stoppingToken.ThrowIfCancellationRequested();
                Update(job with { Status = "running" });

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromHours(4));
                    job = await ArchiveAsync(job, timeout.Token);
                    Update(job);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Update(job with { Status = "queued" });
                    throw;
                }
                catch (Exception ex)
                {
                    _log($"YouTube archive {job.Id} failed: {ex}");
                    Update(job with
                    {
                        Status = "failed",
                        ProtectedCookies = null,
                        Error = ex is OperationCanceledException ? "Archival timed out after four hours." : ex.Message
                    });
                }
            }
        }
    }

    private async Task<YoutubeArchiveJob> ArchiveAsync(YoutubeArchiveJob job, CancellationToken cancellationToken)
    {
        string mediaDirectory = Path.Combine(job.Mode == "audio" ? "Audio" : "Video",
            job.VideoId + (job.Mode == "video" ? $"-{job.MaxHeight?.ToString() ?? "best"}" : ""));
        string relative = job.ArchiveDirectory ?? mediaDirectory;
        string destination = Path.Combine(_directory, relative);
        string staging = Path.Combine(_directory, ".incomplete", job.Id.ToString("N"));
        string marker = Path.Combine(destination, ".mihubot-media");
        string name;

        // A crash after the atomic move but before saving job state must not download again.
        if (System.IO.File.Exists(marker))
        {
            name = await System.IO.File.ReadAllTextAsync(marker, cancellationToken);

            if (Path.GetFileName(name) != name || !System.IO.File.Exists(Path.Combine(destination, name)))
            {
                throw new IOException("The completed archive marker refers to a missing or invalid media file.");
            }
        }
        else
        {
            Directory.CreateDirectory(staging);
            YoutubeArchiveCookie[] cookies = job.ProtectedCookies is not null
                ? JsonSerializer.Deserialize<YoutubeArchiveCookie[]>(_cookiesProtector.Unprotect(job.ProtectedCookies))
                : null;
            string downloaded = await _downloader.DownloadAsync(job, staging, cancellationToken, cookies);
            string channelDirectory = Path.GetDirectoryName(Path.GetFullPath(downloaded));
            string channel = Path.GetRelativePath(staging, channelDirectory);

            if (channel is "." or ".." || Path.IsPathRooted(channel) ||
                channel.AsSpan().ContainsAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                throw new IOException("The downloaded media must be inside a single channel folder.");
            }

            relative = Path.Combine(channel, mediaDirectory);
            destination = Path.Combine(_directory, relative);
            name = Path.GetFileName(downloaded);
            await System.IO.File.WriteAllTextAsync(Path.Combine(channelDirectory, ".mihubot-media"), name, cancellationToken);
            job = job with { Status = "running", ArchiveDirectory = relative };
            Update(job);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(channelDirectory, destination);
        }

        return job with
        {
            Status = "completed",
            ArchiveDirectory = relative,
            File = Path.Combine(relative, name),
            Error = null,
            Warning = null,
            ProtectedCookies = null
        };
    }

    private void Update(YoutubeArchiveJob job) => _store.Modify(state =>
    {
        int index = state.Jobs.FindIndex(j => j.Id == job.Id);
        state.Jobs[index] = job;
    });

    public override void Dispose()
    {
        _metadataLock.Dispose();
        base.Dispose();
    }
}
