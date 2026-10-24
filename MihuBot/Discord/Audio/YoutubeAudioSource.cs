using YoutubeExplode.Videos;

namespace MihuBot.Discord.Audio;

public sealed class YoutubeAudioSource : AudioSourceBase
{
    private readonly HttpClient _http = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.Zero
    });

    private IVideo _video;
    private HttpResponseMessage _downloadResponse;
    private Stream _downloadStream;
    private Stream _ffmpegOutputStream;
    private Process _process;
    private Task _pumpTask = Task.CompletedTask;
    private bool _processStarted;
    private long _bytesRead;

    public YoutubeAudioSource(SocketGuildUser requester, IVideo video, bool isAutoplay = false)
        : base(requester, isAutoplay)
    {
        _video = video;
    }

    protected override async Task InitializeAsync(int bitrateHintKbit, CancellationToken cancellationToken)
    {
        YoutubeDl.YoutubeDlMetadata ytMetadata = await YoutubeDl.GetMetadataAsync(_video.Url, cancellationToken);
        _video = YoutubeHelper.Transform(_video.Id, ytMetadata);
        var bestAudio = ytMetadata.GetBestAudio()
            ?? throw new InvalidOperationException("yt-dlp returned no playable audio format.");

        _downloadResponse = await OpenDownloadAsync(_http, ytMetadata, bestAudio, cancellationToken);
        var downloadStream = await _downloadResponse.Content.ReadAsStreamAsync(cancellationToken);

        double estimatedSourceBitrateKbit = bestAudio.Tbr ?? bestAudio.Abr ?? 128;
        double durationToBuffer = Math.Clamp(ytMetadata.Duration, 60, 11 * 60 * 60); // 1 minute - 11 hours

        double capacity = estimatedSourceBitrateKbit * 1024 / 8 * durationToBuffer;
        capacity = Math.Clamp(capacity, 64 * 1024, 1024 * 1024 * 1024); // 64 KB - 1 GB

        _downloadStream = new ReadAheadStream(downloadStream, (long)capacity);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel warning -rtbufsize 1M -i - -filter:a loudnorm=i=-14 -ac {OpusConstants.Channels} -f s16le -ar {OpusConstants.SamplingRate} -")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };

        _processStarted = _process.Start();

        if (!_processStarted)
        {
            throw new InvalidOperationException("Could not start FFmpeg.");
        }

        _ffmpegOutputStream = _process.StandardOutput.BaseStream;
        _pumpTask = PumpInputAsync(_downloadStream, _process.StandardInput.BaseStream, LifetimeCancellationToken);
    }

    internal static async Task<HttpResponseMessage> OpenDownloadAsync(HttpClient http, YoutubeDl.YoutubeDlMetadata metadata,
        YoutubeDl.YoutubeDlFormat format, CancellationToken cancellationToken, TimeProvider timeProvider = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, format.Url);

        foreach (var header in (metadata.HttpHeaders ?? []).Concat(format.HttpHeaders ?? []))
        {
            if (header.Key.AsSpan().ContainsAny('\r', '\n', '\0') ||
                header.Value.AsSpan().ContainsAny('\r', '\n', '\0'))
            {
                throw new InvalidOperationException("yt-dlp returned a download header containing a forbidden control character.");
            }

            request.Headers.Remove(header.Key);

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException($"Cannot apply yt-dlp download header '{header.Key}'.");
            }
        }

        if (format.AvailableAt is long availableAt)
        {
            timeProvider ??= TimeProvider.System;
            TimeSpan delay = DateTimeOffset.FromUnixTimeSeconds(availableAt) - timeProvider.GetUtcNow();

            if (delay > TimeSpan.Zero)
            {
                // YouTube can reject the signed URL until its pre-playback wait has elapsed.
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }

        HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    internal static async Task PumpInputAsync(Stream download, Stream input, CancellationToken cancellationToken)
    {
        await using (input)
        {
            await download.CopyToAsync(input, cancellationToken);
        }
    }

    public override TimeSpan? Duration => _video.Duration;

    public override TimeSpan? Remaining
    {
        get
        {
            if (Duration is not { } duration)
            {
                return null;
            }

            TimeSpan consumed = TimeSpan.FromMilliseconds(Volatile.Read(ref _bytesRead) / OpusConstants.BytesPerMs);
            return duration.Subtract(consumed);
        }
    }

    public override string Description => _video.Title;

    public override string Author => _video.Author.ChannelTitle;

    public override string Url => _video.Url;

    public override string ThumbnailUrl => _video.Thumbnails.OrderBy(t => t.Resolution.Area).FirstOrDefault()?.Url;

    public override async ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken)
    {
        int read = await _ffmpegOutputStream.ReadAsync(pcmBuffer, cancellationToken);

        if (read == 0)
        {
            await _pumpTask.WaitAsync(cancellationToken);
            await _process.WaitForExitAsync(cancellationToken);

            if (_process.ExitCode != 0)
            {
                throw new IOException($"FFmpeg exited with code {_process.ExitCode} for '{Description}'.");
            }
        }

        Interlocked.Add(ref _bytesRead, read);
        return read;
    }

    protected override async ValueTask DisposeResourcesAsync()
    {
        var cleanup = new AsyncCleanup();

        if (_processStarted)
        {
            cleanup.Run(() =>
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            });
        }

        if (_downloadStream is Stream downloadStream)
        {
            await cleanup.RunAsync(() => downloadStream.DisposeAsync().AsTask());
        }

        cleanup.Run(() => _downloadResponse?.Dispose());

        await cleanup.RunAsync(async () =>
        {
            try
            {
                await _pumpTask;
            }
            catch (Exception ex) when (LifetimeCancellationToken.IsCancellationRequested &&
                ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closing a source interrupts both the download and FFmpeg's input pipe.
            }
        });

        if (_processStarted)
        {
            await cleanup.RunAsync(() => _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        if (_ffmpegOutputStream is Stream output)
        {
            await cleanup.RunAsync(() => output.DisposeAsync().AsTask());
        }

        cleanup.Run(() => _process?.Dispose());
        cleanup.Run(_http.Dispose);
        cleanup.ThrowIfFailed();
    }

    public void DebugDump(StringBuilder sb)
    {
        sb.AppendLine($"FFmpeg arguments: {_process?.StartInfo.Arguments ?? "N/A"}");
    }
}
