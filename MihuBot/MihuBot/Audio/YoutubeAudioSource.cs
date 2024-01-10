﻿using YoutubeExplode.Videos;

namespace MihuBot.Audio;

public sealed class YoutubeAudioSource : AudioSourceBase
{
    private readonly HttpClient _http = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.Zero
    });

    private readonly IVideo _video;
    private Stream _downloadStream;
    private Stream _ffmpegOutputStream;
    private Process _process;
    private TimeSpan? _duration;
    private long _bytesRead;

    public YoutubeAudioSource(SocketGuildUser requester, IVideo video)
        : base(requester)
    {
        _video = video;
        _duration = video.Duration;
    }

    public override async Task InitializeAsync(int bitrateHintKbit, CancellationToken cancellationToken)
    {
        YoutubeDl.YoutubeDlMetadata ytMetadata = await YoutubeDl.GetMetadataAsync(_video.Url);
        _duration ??= TimeSpan.FromSeconds(ytMetadata.Duration);
        var bestAudio = ytMetadata.GetBestAudio();

        var downlaodStream = await _http.GetStreamAsync(bestAudio.Url, cancellationToken);
        _downloadStream = new ReadAheadStream(downlaodStream);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel warning -i - -ac {OpusConstants.Channels} -f s16le -ar {OpusConstants.SamplingRate} -")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };

        _process.Start();

        _ffmpegOutputStream = _process.StandardOutput.BaseStream;

        _ = Task.Run(async () =>
        {
            try
            {
                await _downloadStream.CopyToAsync(_process.StandardInput.BaseStream, cancellationToken);
            }
            catch { }
        }, CancellationToken.None);
    }

    public override TimeSpan? Remaining
    {
        get
        {
            if (_duration is null)
            {
                return null;
            }

            TimeSpan consumed = TimeSpan.FromMilliseconds(Volatile.Read(ref _bytesRead) / OpusConstants.BytesPerMs);
            return _duration.Value.Subtract(consumed);
        }
    }

    public override string Description => _video.Title;

    public override string Url => _video.Url;

    public override string ThumbnailUrl => _video.Thumbnails.OrderBy(t => t.Resolution.Area).FirstOrDefault()?.Url;

    public override async ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken)
    {
        int read = await _ffmpegOutputStream.ReadAsync(pcmBuffer, cancellationToken);
        Interlocked.Add(ref _bytesRead, read);
        return read;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        try
        {
            if (_process is Process process)
            {
                process.Kill();
                process.Dispose();
            }
        }
        catch { }

        if (_downloadStream is Stream downloadStream)
        {
            await downloadStream.DisposeAsync();
        }

        _http.Dispose();
    }

    public void DebugDump(StringBuilder sb)
    {
        sb.AppendLine($"FFmpeg arguments: {_process?.StartInfo.Arguments ?? "N/A"}");
    }
}
