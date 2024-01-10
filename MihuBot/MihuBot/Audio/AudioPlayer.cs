using Discord.Audio;
using Discord.Rest;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using YoutubeExplode.Common;

namespace MihuBot.Audio;

public sealed class AudioPlayer : IAsyncDisposable
{
    public DiscordSocketClient Client { get; }
    public SocketGuild Guild { get; }
    public SocketVoiceChannel VoiceChannel { get; private set; }
    public GuildAudioSettings AudioSettings { get; private set; }

    public SocketTextChannel LastTextChannel { get; set; }

    private readonly AudioScheduler _scheduler = new();
    private readonly CancellationTokenSource _disposedCts = new();
    private readonly ConcurrentDictionary<ulong, AudioPlayer> _audioPlayers;
    private readonly Logger _logger;
    private RestUserMessage _lastSentStatusMessage;
    private int _disposed;
    private TaskCompletionSource _pausedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IAudioClient _audioClient;
    private AudioOutStream _pcmStream;

    private const int CopyLoopTimings = 512;
    private readonly Queue<(string Event, float DeltaMs)> _copyLoopTimings = new(CopyLoopTimings);

    public AudioPlayer(DiscordSocketClient client, GuildAudioSettings audioSettings, Logger logger, SocketGuild guild, SocketTextChannel lastTextChannel, ConcurrentDictionary<ulong, AudioPlayer> audioPlayers)
    {
        Client = client;
        AudioSettings = audioSettings;
        _logger = logger;
        Guild = guild;
        LastTextChannel = lastTextChannel;
        _audioPlayers = audioPlayers;
    }

    public async Task JoinChannelAsync(SocketVoiceChannel voiceChannel)
    {
        try
        {
            int bitrate = Math.Clamp(voiceChannel.Bitrate, GlobalAudioSettings.MinBitrate, GlobalAudioSettings.MaxBitrate);
            _scheduler.SetBitrate(bitrate);

            VoiceChannel = voiceChannel;
            _audioClient = await voiceChannel.ConnectAsync();

            _pcmStream = _audioClient.CreateDirectPCMStream(AudioApplication.Music, bitrate, GlobalAudioSettings.PacketLoss);

            _ = Task.Run(async () =>
            {
                try
                {
                    await CopyAudioAsync();
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync(ex.ToString());
            await DisposeAsync();
        }
    }

    public bool Unpause()
    {
        return Interlocked.Exchange(ref _pausedTcs, null)?.TrySetResult() ?? false;
    }

    public bool Pause()
    {
        if (_pausedTcs is null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Interlocked.CompareExchange(ref _pausedTcs, tcs, null) is null;
        }
        return false;
    }

    public async Task SkipAsync()
    {
        await _scheduler.SkipAsync();
        Unpause();
    }

    public void Enqueue(IAudioSource audioSource)
    {
        _scheduler.Enqueue(audioSource);
        Unpause();
    }

    private async Task SendCurrentlyPlayingAsync()
    {
        if (_scheduler.TryPeekCurrent(out IAudioSource audioSource))
        {
            try
            {
                RestUserMessage lastSentMessage = _lastSentStatusMessage;

                string escapedTitle = audioSource.Description
                    .Replace("[", "\\[", StringComparison.Ordinal)
                    .Replace("]", "\\]", StringComparison.Ordinal);

                int queueLength = _scheduler.QueueLength;

                Embed embed = new EmbedBuilder()
                    .WithTitle("**Now playing**")
                    .WithDescription($"[{escapedTitle}]({audioSource.Url})" +
                        $"\nRequested by {MentionUtils.MentionUser(audioSource.Requester.Id)}" +
                        (queueLength > 0 ? $"\nSongs in queue: {queueLength}" : ""))
                    .WithUrl(audioSource.Url)
                    .WithColor(0x00, 0x42, 0xFF)
                    .WithThumbnailUrl(audioSource.ThumbnailUrl)
                    .Build();

                if (lastSentMessage is null ||
                    lastSentMessage.Channel.Id != LastTextChannel.Id ||
                    (LastTextChannel.GetCachedMessages(1).FirstOrDefault() is SocketMessage message
                        ? message.Author.Id != Client.CurrentUser.Id
                        : DateTime.UtcNow.Subtract(lastSentMessage.Timestamp.UtcDateTime) > TimeSpan.FromMinutes(5)))
                {
                    _lastSentStatusMessage = await LastTextChannel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);

                    if (lastSentMessage is not null)
                    {
                        await lastSentMessage.TryDeleteAsync();
                    }
                }
                else
                {
                    await lastSentMessage.ModifyAsync(message => message.Embed = embed);
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    await _logger.DebugAsync(ex.ToString());
                }
            }
        }
    }

    private async Task CopyAudioAsync()
    {
        const int FramesPerBuffer = 2;
        byte[] readBuffer = new byte[OpusConstants.FrameBytes * FramesPerBuffer];

        IAudioSource previous = null;
        Task sendCurrentlyPlayingTask = Task.CompletedTask;

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan lastElapsed = stopwatch.Elapsed;

        await using var pcmScheduler = new PcmAudioScheduler(_audioClient, _pcmStream, LogTiming)
        {
            AudioSettings = AudioSettings
        };

        while (!_disposedCts.IsCancellationRequested)
        {
            LogTiming("Start");

            if (_pausedTcs is TaskCompletionSource pausedTcs)
            {
                LogTiming("Paused");
                await pausedTcs.Task;
                LogTiming("Unpaused");
                Interlocked.CompareExchange(ref _pausedTcs, null, pausedTcs);
                continue;
            }

            IAudioSource audioSource = await _scheduler.GetAudioSourceAsync(_disposedCts.Token);

            if (!ReferenceEquals(previous, audioSource))
            {
                previous = audioSource;
                await sendCurrentlyPlayingTask.WaitAsync(_disposedCts.Token);
                sendCurrentlyPlayingTask = SendCurrentlyPlayingAsync();
            }

            LogTiming("Read start");

            int read = 0;
            try
            {
                read = await audioSource.ReadAsync(readBuffer, _disposedCts.Token);
            }
            catch (Exception ex)
            {
                _logger.DebugLog(ex.ToString(), guildId: Guild.Id, channelId: VoiceChannel.Id);
            }

            if (read <= 0)
            {
                await pcmScheduler.ClearAsync();
                _scheduler.SkipCurrent(audioSource);
                await audioSource.DisposeAsync();
                continue;
            }

            try
            {
                LogTiming("Write buffer start");
                await pcmScheduler.WritePcmSamplesAsync(readBuffer.AsMemory(0, read), _disposedCts.Token);
            }
            catch
            {
                await DisposeAsync();
            }
        }

        void LogTiming(string eventName)
        {
            TimeSpan elapsed = stopwatch.Elapsed;
            TimeSpan delta = elapsed - lastElapsed;
            lastElapsed = elapsed;
            lock (_copyLoopTimings)
            {
                if (_copyLoopTimings.Count == CopyLoopTimings) _copyLoopTimings.Dequeue();
                _copyLoopTimings.Enqueue((eventName, (float)delta.TotalMilliseconds));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _scheduler.DisposeAsync();

        _disposedCts.Cancel();
        Unpause();
        _audioPlayers.TryRemove(new KeyValuePair<ulong, AudioPlayer>(Guild.Id, this));
    }

    public void DebugDump(StringBuilder sb)
    {
        Property(sb, "VoiceChannel", VoiceChannel.Name);
        Property(sb, "VC Bitrate", VoiceChannel.Bitrate.ToString());
        Property(sb, "Volume", AudioSettings.Volume?.ToString() ?? "N/A");
        Property(sb, "QueueLength", _scheduler.QueueLength.ToString());

        IAudioSource[] sources = _scheduler.GetQueueSnapshot(limit: -1, out IAudioSource current);

        if (current is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Current audio source:");
            AudioSource(sb, current);
        }

        foreach (IAudioSource audioSource in sources)
        {
            sb.AppendLine();
            AudioSource(sb, audioSource);
        }

        sb.AppendLine();
        sb.AppendLine("Copy loop deltas:");
        lock (_copyLoopTimings)
        {
            foreach ((string eventName, float deltaMs) in _copyLoopTimings)
            {
                sb.AppendLine($"{(int)deltaMs,-4} {eventName}");
            }
        }

        static void AudioSource(StringBuilder sb, IAudioSource audioSource)
        {
            Property(sb, nameof(audioSource.Requester), audioSource.Requester.Username);
            Property(sb, nameof(audioSource.Url), audioSource.Url);
            Property(sb, nameof(audioSource.ThumbnailUrl), audioSource.ThumbnailUrl);
            Property(sb, nameof(audioSource.Remaining), audioSource.Remaining?.ToString() ?? "N/A");
            Property(sb, nameof(audioSource.Description), audioSource.Description);
            audioSource.DebugDump(sb);
        }

        static void Property(StringBuilder sb, string name, string value)
        {
            sb.Append(name);
            sb.Append(": ");
            sb.Append(' ', Math.Max(0, 15 - name.Length));
            sb.Append(value);
            sb.AppendLine();
        }
    }

    public async Task PostQueueAsync()
    {
        var builder = new EmbedBuilder()
            .WithTitle("Queue")
            .WithColor(0x00, 0x42, 0xFF);

        IAudioSource[] sources = _scheduler.GetQueueSnapshot(limit: 25, out IAudioSource current);

        if (current is not null)
        {
            string escapedTitle = current.Description
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal);

            builder.WithDescription($"Now playing [{escapedTitle}]({current.Url}) (requested by {MentionUtils.MentionUser(current.Requester.Id)})");
            builder.WithThumbnailUrl(current.ThumbnailUrl);
        }

        int count = 0;
        foreach (IAudioSource audioSource in sources)
        {
            count++;

            string escapedTitle = audioSource.Description
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal);

            builder.AddField($"{count}. {escapedTitle}", $"Requested by {MentionUtils.MentionUser(audioSource.Requester.Id)}", inline: true);
        }

        if (string.IsNullOrEmpty(builder.Title) && builder.Fields.Count == 0)
        {
            await LastTextChannel.SendMessageAsync("The queue is currently empty");
        }
        else
        {
            await LastTextChannel.SendMessageAsync(embed: builder.Build(), allowedMentions: AllowedMentions.None);
        }
    }
}
