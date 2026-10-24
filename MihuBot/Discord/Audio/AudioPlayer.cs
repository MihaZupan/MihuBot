using Discord.Audio;
using Discord.Rest;
using Discord.Net;
using System.Collections.Concurrent;
using System.Globalization;
using YoutubeExplode.Common;

namespace MihuBot.Discord.Audio;

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
    private readonly AudioHistoryService _history;
    private readonly AudioRecommendationService _recommendations;
    private readonly AudioSpeechService _speech;
    private AudioVoiceListener _voiceListener;
    private Task _listeningTask = Task.CompletedTask;
    private Task _autoplayTask = Task.CompletedTask;
    private Task _statusTask = Task.CompletedTask;
    private Task _playbackTask = Task.CompletedTask;
    private readonly Lock _disposeLock = new();
    private Task _disposeTask;
    private long _lastAutoplayAttempt = -1;
    private volatile bool _hasPlayed;
    private AudioPlaybackEntry _playbackEntry;
    private IAudioSource _historySource;
    private IAudioSource _skippedSource;
    private IAudioSource _stoppedSource;
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private RestUserMessage _lastSentStatusMessage;
    private TaskCompletionSource _pausedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IAudioClient _audioClient;
    private AudioOutStream _pcmStream;

    private long _pcmBytesToSkip;

    private const int CopyLoopTimings = 2048;
    private readonly Queue<(string Event, float DeltaMs)> _copyLoopTimings = new(CopyLoopTimings);

    public AudioPlayer(DiscordSocketClient client, GuildAudioSettings audioSettings, Logger logger, SocketGuild guild, SocketTextChannel lastTextChannel,
        ConcurrentDictionary<ulong, AudioPlayer> audioPlayers, AudioHistoryService history, AudioRecommendationService recommendations,
        AudioSpeechService speech = null)
    {
        Client = client;
        AudioSettings = audioSettings;
        _logger = logger;
        Guild = guild;
        LastTextChannel = lastTextChannel;
        _audioPlayers = audioPlayers;
        _history = history;
        _recommendations = recommendations;
        _speech = speech;
    }

    public async Task JoinChannelAsync(SocketVoiceChannel voiceChannel)
    {
        IAudioClient joined = null;
        bool published = false;

        try
        {
            lock (_disposeLock)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            }

            int bitrate = Math.Clamp(voiceChannel.Bitrate, GlobalAudioSettings.MinBitrate, GlobalAudioSettings.MaxBitrate);
            _scheduler.SetBitrate(bitrate);

            VoiceChannel = voiceChannel;
            joined = await voiceChannel.ConnectAsync(selfDeaf: _speech is null);

            lock (_disposeLock)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                _audioClient = joined;
                published = true;
                _pcmStream = _audioClient.CreateDirectPCMStream(AudioApplication.Music, bitrate, GlobalAudioSettings.PacketLoss);

                if (_speech is not null)
                {
                    _listeningTask = Task.Run(StartListeningAsync);
                }

                if (_recommendations.IsAvailable)
                {
                    _autoplayTask = Task.Run(MonitorAutoplayAsync);
                }

                _statusTask = Task.Run(MonitorStatusAsync);

                _playbackTask = Task.Run(async () =>
                {
                    try
                    {
                        await CopyAudioAsync();
                    }
                    catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        _logger.DebugLog(ex.ToString(), guildId: Guild.Id, channelId: VoiceChannel.Id);
                    }
                    finally
                    {
                        _ = DisposeAfterPlaybackAsync();
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.DebugLog(ex.ToString(), guildId: Guild.Id);
            await DisposeAsync();
        }
        finally
        {
            if (!published && joined is not null)
            {
                var cleanup = new AsyncCleanup();
                await cleanup.RunAsync(() => joined.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                cleanup.Run(joined.Dispose);
                cleanup.ThrowIfFailed();
            }
        }
    }

    private async Task StartListeningAsync()
    {
        try
        {
            await _speech.EnsureReadyAsync(_disposedCts.Token);
            await VoiceChannel.SendMessageAsync(
                "I'm listening for spoken music commands while connected. Say \"Hey MihuBot, queue song ...\", pause, resume, skip, or stop.",
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = _disposedCts.Token, Timeout = 10_000 });

            lock (_disposeLock)
            {
                if (_disposeTask is null)
                {
                    _voiceListener = new AudioVoiceListener(this, _audioClient, _speech, _logger);
                }
            }
        }
        catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.DebugLog($"Could not enable spoken music commands: {ex}", guildId: Guild.Id);

            try
            {
                await LastTextChannel.SendMessageAsync("Spoken music commands are unavailable. Music playback and text commands still work.",
                    options: new RequestOptions { CancelToken = _disposedCts.Token, Timeout = 10_000 });
            }
            catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested) { }
            catch (Exception notificationError)
            {
                _logger.DebugLog($"Could not report spoken command setup failure: {notificationError}", guildId: Guild.Id);
            }
        }
    }

    internal IAudioSource[] GetSpokenQueueSnapshot() => _scheduler.GetPlaybackSnapshot();

    internal async Task<bool> HandleSpokenCommandAsync(ulong userId, SpokenAudioCommand command, long spokenAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SocketGuildUser user = VoiceChannel.GetUser(userId);

        if (user is null || user.IsBot || _disposedCts.IsCancellationRequested)
        {
            return false;
        }

        string response;

        switch (command.Action)
        {
            case SpokenAudioAction.Queue:
                SpokenSongResolution resolution = await _speech.ResolveSongAsync(Guild.Id, command, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (_disposedCts.IsCancellationRequested || VoiceChannel.GetUser(userId) is null)
                {
                    return false;
                }

                if (resolution.Video is null)
                {
                    await SendSpokenCommandFailureAsync(LastTextChannel, user.Id, resolution.Error, cancellationToken);
                    return false;
                }
                else
                {
                    lock (_disposeLock)
                    {
                        if (_disposeTask is not null)
                        {
                            return false;
                        }

                        Enqueue(new YoutubeAudioSource(user, resolution.Video));
                    }

                    response = $"Queued {Format.Code(resolution.Video.Title)}.";
                }

                break;
            case SpokenAudioAction.Pause:
                Pause();
                response = "Paused.";
                break;
            case SpokenAudioAction.Resume:
                Unpause();
                response = "Resumed.";
                break;
            case SpokenAudioAction.Skip:
            case SpokenAudioAction.SkipUntil:
                if (_scheduler.WasSkippedSince(spokenAt))
                {
                    return false;
                }

                if (command.Action == SpokenAudioAction.SkipUntil && command.TargetSource is null)
                {
                    await SendSpokenCommandFailureAsync(LastTextChannel, user.Id, "I couldn't find that track in the queue.", cancellationToken);
                    return false;
                }

                AudioSkipBatch skipped = await SkipSpokenTracksAsync(command.Count, spokenAt, command.TargetSource);

                if (skipped.Ignored)
                {
                    return false;
                }

                if (skipped.TargetMissing)
                {
                    await SendSpokenCommandFailureAsync(LastTextChannel, user.Id,
                        "That track is no longer in the queue. Please repeat your request.", cancellationToken);
                    return false;
                }

                if (command.Action == SpokenAudioAction.SkipUntil)
                {
                    response = skipped.Count == 0
                        ? $"{Format.Code(command.TargetSource.Description)} is already current or next."
                        : $"Skipped {skipped.Count} song(s). Next is {Format.Code(command.TargetSource.Description)}.";
                }
                else if (skipped.Count < command.Count)
                {
                    await SendSpokenCommandFailureAsync(LastTextChannel, user.Id,
                        skipped.Count == 0 ? "There are no songs to skip." : $"Only {skipped.Count} song(s) were available; I skipped those.",
                        cancellationToken);
                    return skipped.Count > 0;
                }
                else
                {
                    response = skipped.Count == 1 ? "Skipped." : $"Skipped {skipped.Count} songs.";
                }

                break;
            case SpokenAudioAction.Stop:
                try
                {
                    await LastTextChannel.SendMessageAsync("Stopped music and voice listening.", allowedMentions: AllowedMentions.None,
                        options: new RequestOptions { CancelToken = cancellationToken, Timeout = 10_000 });
                }
                finally
                {
                    await DisposeAsync();
                }

                return true;
            case SpokenAudioAction.None:
            default:
                throw new InvalidOperationException($"Unknown spoken audio action '{command.Action}'.");
        }

        await LastTextChannel.SendMessageAsync($"{user.DisplayName}: {response}", allowedMentions: AllowedMentions.None,
            options: new RequestOptions { CancelToken = cancellationToken, Timeout = 10_000 });
        return true;
    }

    private async Task<AudioSkipBatch> SkipSpokenTracksAsync(int count, long spokenAt, IAudioSource target)
    {
        AudioSkipBatch batch;

        lock (_disposeLock)
        {
            if (_disposeTask is not null)
            {
                return new(0, null, [], Ignored: true);
            }

            batch = _scheduler.SkipTracks(count, spokenAt, target, current => Volatile.Write(ref _skippedSource, current));
        }

        var cleanup = new AsyncCleanup();

        if (batch.Current is not null)
        {
            await cleanup.RunAsync(() => FinishPlaybackAsync(AudioPlaybackOutcome.Skipped, batch.Current));
        }

        await cleanup.RunAsync(() => AudioScheduler.DisposeSourcesAsync(batch.ToDispose));

        if (batch.Count > 0)
        {
            Unpause();
        }

        cleanup.ThrowIfFailed();
        return batch;
    }

    internal static Task SendSpokenCommandFailureAsync(IMessageChannel channel, ulong userId, string message, CancellationToken cancellationToken) =>
        channel.SendMessageAsync($"{MentionUtils.MentionUser(userId)}: {message}",
            allowedMentions: new AllowedMentions { UserIds = [userId] },
            options: new RequestOptions { CancelToken = cancellationToken, Timeout = 10_000 });

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
        try
        {
            if (_scheduler.TryPeekCurrent(out IAudioSource current))
            {
                Volatile.Write(ref _skippedSource, current);
                await SkipSourceAsync(current, AudioPlaybackOutcome.Skipped);
            }
            else
            {
                await _scheduler.SkipAsync();
            }
        }
        catch (AudioSourceException ex)
        {
            _logger.DebugLog(ex.ToString(), guildId: Guild.Id);
        }
        finally
        {
            Unpause();
        }
    }

    private async Task SkipSourceAsync(IAudioSource source, AudioPlaybackOutcome outcome)
    {
        try
        {
            if (!await _scheduler.TrySkipAsync(source))
            {
                return;
            }
        }
        catch (AudioSourceException ex)
        {
            _logger.DebugLog(ex.ToString(), guildId: Guild.Id);
        }

        await FinishPlaybackAsync(outcome, source);
    }

    private async Task ClearAutoplayAsync(bool invalidate = false)
    {
        try
        {
            await _scheduler.ClearAutoplayAsync(invalidate);
        }
        catch (AudioSourceException ex)
        {
            _logger.DebugLog(ex.ToString(), guildId: Guild.Id);
        }
    }

    private bool HasHumanListeners() => VoiceChannel.ConnectedUsers.Any(user => !user.IsBot);

    private async Task MonitorAutoplayAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        CancellationTokenSource requestCts = null;
        Task request = Task.CompletedTask;
        long requestRevision = -1;

        try
        {
            while (await timer.WaitForNextTickAsync(_disposedCts.Token))
            {
                bool queueEmpty = _scheduler.GetAutoplayState(out IAudioSource current, out long revision);
                bool listeners = HasHumanListeners();
                bool active = listeners && _pausedTcs is null;
                bool nearEnd = current?.Remaining is { } remaining && remaining <= TimeSpan.FromSeconds(30);
                bool eligible = active && _hasPlayed && queueEmpty &&
                    (current is null || (ReferenceEquals(current, _historySource) && nearEnd));

                if (!eligible || requestRevision != revision)
                {
                    requestCts?.Cancel();
                }

                if (!active || !queueEmpty)
                {
                    await ClearAutoplayAsync();
                }

                if (!listeners && current is { IsAutoplay: true })
                {
                    Volatile.Write(ref _stoppedSource, current);
                    await SkipSourceAsync(current, AudioPlaybackOutcome.Stopped);
                }

                if (!active)
                {
                    Interlocked.Exchange(ref _lastAutoplayAttempt, -1);
                }

                if (!request.IsCompleted)
                {
                    continue;
                }

                await request;
                requestCts?.Dispose();
                requestCts = null;

                if (eligible && !_scheduler.HasAutoplay && Interlocked.Read(ref _lastAutoplayAttempt) != revision)
                {
                    Interlocked.Exchange(ref _lastAutoplayAttempt, revision);
                    requestRevision = revision;
                    requestCts = CancellationTokenSource.CreateLinkedTokenSource(_disposedCts.Token);
                    request = PrepareAutoplayAsync(revision, requestCts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.DebugLog($"AI autoplay monitor failed: {ex}", guildId: Guild.Id);
        }
        finally
        {
            requestCts?.Cancel();
            await request;
            requestCts?.Dispose();
        }
    }

    private async Task PrepareAutoplayAsync(long revision, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            var video = await _recommendations.RecommendAsync(Guild.Id, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            var source = new YoutubeAudioSource(null, video, isAutoplay: true);

            if (_pausedTcs is not null || !HasHumanListeners() ||
                !_scheduler.TrySetAutoplay(source, revision))
            {
                await source.DisposeAsync();
                return;
            }

            if (!await source.EnsureInitializedAsync().WaitAsync(timeout.Token))
            {
                throw new InvalidOperationException($"AI autoplay could not initialize '{video.Title}'.");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested || _scheduler.Revision != revision)
        {
            await ClearAutoplayAsync();

            if (!cancellationToken.IsCancellationRequested && _scheduler.Revision == revision)
            {
                _logger.DebugLog("AI autoplay recommendation timed out.", guildId: Guild.Id);
                await NotifyAutoplayFailureAsync();
            }
        }
        catch (Exception ex)
        {
            await ClearAutoplayAsync();
            _logger.DebugLog($"AI autoplay failed: {ex}", guildId: Guild.Id);

            if (!cancellationToken.IsCancellationRequested && _scheduler.Revision == revision)
            {
                await NotifyAutoplayFailureAsync();
            }
        }
    }

    private async Task NotifyAutoplayFailureAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposedCts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            if (!_disposedCts.IsCancellationRequested)
            {
                await LastTextChannel.SendMessageAsync("AI autoplay couldn't prepare a song. Add a song manually to try again.",
                    options: new RequestOptions { CancelToken = timeout.Token }).WaitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.DebugLog($"Could not report AI autoplay failure: {ex}", guildId: Guild.Id);
        }
    }

    public async Task SkipInCurrentSourceAsync(TimeSpan duration)
    {
        if (!_scheduler.TryPeekCurrent(out IAudioSource audioSource))
        {
            return;
        }

        if (audioSource.Remaining is { } remaining && remaining <= duration)
        {
            await SkipAsync();
        }
        else
        {
            long numberOfFrames = (long)(duration.TotalSeconds * OpusConstants.FramesPerSecond);
            Interlocked.Add(ref _pcmBytesToSkip, numberOfFrames * OpusConstants.FrameBytes);
            Unpause();
        }
    }

    public void Enqueue(IAudioSource audioSource)
    {
        _scheduler.Enqueue(audioSource);
        Unpause();
    }

    private async Task MonitorStatusAsync()
    {
        try
        {
            await RunStatusUpdatesAsync(_scheduler, () => _pausedTcs is not null, SendStatusAsync, _disposedCts.Token);
        }
        catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.DebugLog($"Audio status monitor failed: {ex}", guildId: Guild.Id);
        }
    }

    internal static async Task RunStatusUpdatesAsync(AudioScheduler scheduler, Func<bool> isPaused,
        Func<Embed, bool, Task<bool>> sendStatus, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        IAudioSource previous = null;
        long previousRevision = -1;
        bool previouslyPaused = false;
        bool hasStatus = false;
        long lastUpdate = Stopwatch.GetTimestamp();

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            long revision = scheduler.Revision;
            IAudioSource[] sources = scheduler.GetQueueSnapshot(limit: 25, out IAudioSource current);
            bool paused = isPaused();
            bool sourceChanged = !ReferenceEquals(previous, current);
            bool changed = sourceChanged || revision != previousRevision || paused != previouslyPaused;

            if (!changed && (current is null || paused || Stopwatch.GetElapsedTime(lastUpdate) < TimeSpan.FromSeconds(5)))
            {
                continue;
            }

            if (current is not null || sources.Length > 0 || hasStatus)
            {
                Embed embed = BuildStatusEmbed(current is null ? "Nothing playing" : paused ? "Paused" : "Now playing", current, sources);

                if (!await sendStatus(embed, sourceChanged && current is not null))
                {
                    continue;
                }

                hasStatus = true;
            }

            previous = current;
            previousRevision = revision;
            previouslyPaused = paused;
            lastUpdate = Stopwatch.GetTimestamp();
        }
    }

    private async Task<bool> SendStatusAsync(Embed embed, bool newTrack)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposedCts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var options = new RequestOptions { CancelToken = timeout.Token };

        try
        {
            RestUserMessage lastSentMessage = _lastSentStatusMessage;

            if (lastSentMessage is null ||
                lastSentMessage.Channel.Id != LastTextChannel.Id ||
                (newTrack && (LastTextChannel.GetCachedMessages(1).FirstOrDefault() is SocketMessage message
                    ? message.Author.Id != Client.CurrentUser.Id
                    : DateTime.UtcNow.Subtract(lastSentMessage.Timestamp.UtcDateTime) > TimeSpan.FromMinutes(5))))
            {
                _lastSentStatusMessage = await LastTextChannel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None, options: options)
                    .WaitAsync(timeout.Token);

                if (lastSentMessage is not null)
                {
                    await lastSentMessage.TryDeleteAsync().WaitAsync(timeout.Token);
                }
            }
            else
            {
                try
                {
                    await lastSentMessage.ModifyAsync(message => message.Embed = embed, options).WaitAsync(timeout.Token);
                }
                catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownMessage)
                {
                    _lastSentStatusMessage = await LastTextChannel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None, options: options)
                        .WaitAsync(timeout.Token);
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (_disposedCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.DebugLog(ex.ToString(), guildId: Guild.Id);
        }

        return false;
    }

    private async Task CopyAudioAsync()
    {
        try
        {
            await CopyAudioCoreAsync();
        }
        finally
        {
            await FinishPlaybackAsync(AudioPlaybackOutcome.Stopped);
        }
    }

    private async Task RecordPlaybackAsync(IAudioSource source)
    {
        await _historyLock.WaitAsync();

        try
        {
            if (_playbackEntry is not null ||
                !_scheduler.TryPeekCurrent(out IAudioSource current) || !ReferenceEquals(current, source))
            {
                return;
            }

            _playbackEntry = await _history.StartAsync(Guild.Id, source);
            _historySource = source;
            _hasPlayed = true;

            if (ReferenceEquals(Volatile.Read(ref _skippedSource), source) ||
                ReferenceEquals(Volatile.Read(ref _stoppedSource), source))
            {
                await FinishPlaybackCoreAsync(AudioPlaybackOutcome.Stopped);
            }
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task FinishPlaybackAsync(AudioPlaybackOutcome outcome, IAudioSource expectedSource = null)
    {
        await _historyLock.WaitAsync();

        try
        {
            if (expectedSource is null || ReferenceEquals(_historySource, expectedSource))
            {
                await FinishPlaybackCoreAsync(outcome);
            }
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task FinishPlaybackCoreAsync(AudioPlaybackOutcome outcome)
    {
        if (_playbackEntry is null)
        {
            return;
        }

        if (ReferenceEquals(Volatile.Read(ref _skippedSource), _historySource))
        {
            outcome = AudioPlaybackOutcome.Skipped;
        }
        else if (ReferenceEquals(Volatile.Read(ref _stoppedSource), _historySource))
        {
            outcome = AudioPlaybackOutcome.Stopped;
        }

        await _history.FinishAsync(_playbackEntry, outcome);
        _playbackEntry = null;
        _historySource = null;
    }

    private async Task CopyAudioCoreAsync()
    {
        const int FramesPerBuffer = 10;
        byte[] readBuffer = new byte[OpusConstants.FrameBytes * FramesPerBuffer];

        IAudioSource previous = null;

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan lastElapsed = stopwatch.Elapsed;

        await using var pcmScheduler = new PcmAudioScheduler(_audioClient, _pcmStream, LogTiming, ex =>
        {
            _logger.DebugLog($"Voice transport failed: {ex}", guildId: Guild.Id);
            _disposedCts.Cancel();
        })
        {
            AudioSettings = AudioSettings
        };

        var audioSourceCts = CancellationTokenSource.CreateLinkedTokenSource(_disposedCts.Token);

        try
        {
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

                IAudioSource audioSource;

                try
                {
                    audioSource = await _scheduler.GetAudioSourceAsync(_disposedCts.Token);
                }
                catch (AudioSourceException ex)
                {
                    _logger.DebugLog(ex.ToString(), guildId: Guild.Id);
                    continue;
                }

                if (_pausedTcs is not null)
                {
                    continue;
                }

                if (!ReferenceEquals(previous, audioSource))
                {
                    await FinishPlaybackAsync(AudioPlaybackOutcome.Stopped);
                    previous = audioSource;
                    Volatile.Write(ref _pcmBytesToSkip, 0);
                }

                LogTiming("Read start");

                int read = 0;
                bool readFailed = false;
                try
                {
                    audioSourceCts.CancelAfter(TimeSpan.FromSeconds(5));

                    read = await audioSource.ReadAsync(readBuffer, audioSourceCts.Token);
                }
                catch (OperationCanceledException) when (audioSourceCts.IsCancellationRequested)
                {
                    readFailed = true;
                    LogTiming("ReadAsync cancelled");
                }
                catch (Exception ex)
                {
                    readFailed = true;
                    _logger.DebugLog(ex.ToString(), guildId: Guild.Id, channelId: VoiceChannel.Id);
                }

                if (!audioSourceCts.TryReset())
                {
                    audioSourceCts.Dispose();
                    audioSourceCts = CancellationTokenSource.CreateLinkedTokenSource(_disposedCts.Token);
                }

                if (read <= 0)
                {
                    LogTiming("Read EOF");
                    bool autoplayFailed = audioSource.IsAutoplay &&
                        (readFailed || !ReferenceEquals(_historySource, audioSource)) &&
                        !ReferenceEquals(Volatile.Read(ref _skippedSource), audioSource) &&
                        !ReferenceEquals(Volatile.Read(ref _stoppedSource), audioSource) &&
                        !_disposedCts.IsCancellationRequested;

                    AudioPlaybackOutcome outcome = _disposedCts.IsCancellationRequested ? AudioPlaybackOutcome.Stopped :
                        readFailed ? AudioPlaybackOutcome.Failed : AudioPlaybackOutcome.Completed;
                    await FinishPlaybackAsync(outcome);

                    if (autoplayFailed)
                    {
                        await ClearAutoplayAsync(invalidate: true);
                        Interlocked.Exchange(ref _lastAutoplayAttempt, _scheduler.Revision);
                        _logger.DebugLog($"AI autoplay audio failed for '{audioSource.Description}'.", guildId: Guild.Id);
                        await NotifyAutoplayFailureAsync();
                    }

                    _scheduler.SkipCurrent(audioSource);

                    try
                    {
                        await audioSource.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.DebugLog($"Audio source cleanup failed: {ex}", guildId: Guild.Id);
                    }

                    continue;
                }

                await RecordPlaybackAsync(audioSource);

                Memory<byte> pcm = readBuffer.AsMemory(0, read);

                long toSkip = Interlocked.Read(ref _pcmBytesToSkip);
                if (toSkip != 0)
                {
                    toSkip = Math.Min(toSkip, read);
                    pcm = pcm.Slice((int)toSkip);
                    Interlocked.Add(ref _pcmBytesToSkip, -toSkip);

                    if (pcm.IsEmpty)
                    {
                        LogTiming("Skipped PCM buffer");
                        continue;
                    }

                    await pcmScheduler.ClearAsync();
                }

                LogTiming("Write buffer start");
                await pcmScheduler.WritePcmSamplesAsync(pcm, _disposedCts.Token);
            }
        }
        finally
        {
            audioSourceCts.Dispose();
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

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeAfterPlaybackAsync()
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.DebugLog($"Audio player cleanup failed: {ex}", guildId: Guild.Id);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var cleanup = new AsyncCleanup();

        try
        {
            cleanup.Run(_disposedCts.Cancel);
            Unpause();
            await cleanup.RunAsync(() => _listeningTask);

            if (_voiceListener is not null)
            {
                await cleanup.RunAsync(() => _voiceListener.DisposeAsync().AsTask());
            }

            await cleanup.RunAsync(() => _scheduler.DisposeAsync().AsTask());

            if (_audioClient is not null)
            {
                await cleanup.RunAsync(() => _audioClient.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            }

            if (_pcmStream is not null)
            {
                await cleanup.RunAsync(() => _pcmStream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            }

            cleanup.Run(() => _audioClient?.Dispose());
            await cleanup.RunAsync(() => _autoplayTask);
            await cleanup.RunAsync(() => _statusTask);
            await cleanup.RunAsync(() => _playbackTask);
        }
        finally
        {
            _audioPlayers.TryRemove(new KeyValuePair<ulong, AudioPlayer>(Guild.Id, this));
        }

        cleanup.ThrowIfFailed();
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
            Property(sb, nameof(audioSource.Requester), audioSource.Requester?.Username ?? "AI autoplay");
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

    internal static Embed BuildStatusEmbed(string title, IAudioSource current, IAudioSource[] sources)
    {
        var builder = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(0x00, 0x42, 0xFF);

        if (current is not null)
        {
            string escapedTitle = current.Description
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal);

            builder.WithDescription($"Now playing [{escapedTitle}]({current.Url})\n{GetAttribution(current)}\n{GetPlaybackTime(current)}");
            builder.WithUrl(current.Url);
            builder.WithThumbnailUrl(current.ThumbnailUrl);
        }

        builder.WithFooter(sources.Length == 0 ? "The queue is currently empty" : "Up next");

        int count = 0;
        foreach (IAudioSource audioSource in sources)
        {
            count++;

            string escapedTitle = audioSource.Description
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal);

            builder.AddField($"{count}. {escapedTitle}", $"{GetAttribution(audioSource)}\n{GetDuration(audioSource)}", inline: true);
        }

        return builder.Build();
    }

    private static string GetPlaybackTime(IAudioSource source)
    {
        if (source.Remaining is not { } remaining)
        {
            return GetDuration(source) + " (remaining unknown)";
        }

        remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;

        if (source.Duration is not { } duration)
        {
            return $"{FormatDuration(remaining)} remaining (duration unknown)";
        }

        TimeSpan elapsed = duration - remaining;
        return $"{FormatDuration(elapsed)} / {FormatDuration(duration)} ({FormatDuration(remaining)} remaining)";
    }

    private static string GetDuration(IAudioSource source) =>
        source.Duration is { } duration ? FormatDuration(duration) : "Duration unknown";

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalHours >= 1
            ? ((long)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + duration.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string GetAttribution(IAudioSource source) =>
        source.IsAutoplay ? "AI autoplay" : $"Requested by {MentionUtils.MentionUser(source.Requester.Id)}";
}
