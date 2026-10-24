using System.Text.Json;
using Discord.Audio;

#nullable enable

namespace MihuBot.Discord.Audio;

internal sealed class AudioVoiceListener : IAsyncDisposable
{
    private sealed class Capture(AudioInStream stream, CancellationTokenSource cancellation)
    {
        public AudioInStream Stream { get; } = stream;
        public CancellationTokenSource Cancellation { get; } = cancellation;
    }

    private readonly AudioPlayer _player;
    private readonly IAudioClient _client;
    private readonly AudioSpeechService _speech;
    private readonly Logger _logger;
    private readonly Func<ulong, AudioInStream, Task> _streamCreated;
    private readonly Func<ulong, Task> _streamDestroyed;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Capture> _captures = [];
    private readonly HashSet<Task> _tasks = [];
    private Task? _disposeTask;
    private long _lastIssue;

    public AudioVoiceListener(AudioPlayer player, IAudioClient client, AudioSpeechService speech, Logger logger)
    {
        _player = player;
        _client = client;
        _speech = speech;
        _logger = logger;

        _streamCreated = (userId, stream) =>
        {
            OnStreamCreated(userId, stream);
            return Task.CompletedTask;
        };
        _streamDestroyed = userId =>
        {
            OnStreamDestroyed(userId);
            return Task.CompletedTask;
        };
        _client.StreamCreated += _streamCreated;
        _client.StreamDestroyed += _streamDestroyed;

        foreach ((ulong userId, AudioInStream stream) in client.GetStreams())
        {
            OnStreamCreated(userId, stream);
        }
    }

    private void OnStreamCreated(ulong userId, AudioInStream stream)
    {
        lock (_gate)
        {
            if (_disposeTask is not null || _player.VoiceChannel.GetUser(userId) is not { IsBot: false })
            {
                return;
            }

            if (_captures.TryGetValue(userId, out Capture? previous))
            {
                if (ReferenceEquals(previous.Stream, stream))
                {
                    return;
                }

                previous.Cancellation.Cancel();
            }

            var capture = new Capture(stream, CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token));
            _captures[userId] = capture;
            Task task = Task.Run(() => ReadSpeakerAsync(userId, capture));
            _tasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_gate)
                {
                    _tasks.Remove(completed);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void OnStreamDestroyed(ulong userId)
    {
        lock (_gate)
        {
            if (_captures.Remove(userId, out Capture? capture))
            {
                capture.Cancellation.Cancel();
            }
        }
    }

    private async Task ReadSpeakerAsync(ulong userId, Capture capture)
    {
        CancellationToken cancellationToken = capture.Cancellation.Token;
        SpokenAudioCommand? previousCommand = null;
        var context = new SpokenAudioContext((text, token) =>
            _speech.ParseCommandAsync(_player.Guild.Id, text, token, _player.GetSpokenQueueSnapshot()));
        long previousEnd = -1;
        bool overloaded = false;
        var segmenter = new SpeechSegmenter(segment =>
        {
            if (!_speech.TryEnqueue(new(segment.Samples, segment.Timestamp, cancellationToken, async (text, token) =>
            {
                try
                {
                    SpokenAudioCommand? command = await context.ParseAsync(segment, text, token);

                    if (command is null || IsRepeatedWindowCommand(segment, command, previousCommand, previousEnd))
                    {
                        return;
                    }

                    if (await _player.HandleSpokenCommandAsync(userId, command, segment.SpeechEndTimestamp, token))
                    {
                        _logger.DebugLog($"[AudioVoiceCommand] Successfully handled spoken command: {JsonSerializer.Serialize(command)}",
                            guildId: _player.Guild.Id, channelId: _player.VoiceChannel.Id, userId: userId);
                        previousCommand = command;
                        previousEnd = segment.EndSample;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.DebugLog($"Spoken command failed for user {userId}: {ex}", guildId: _player.Guild.Id);
                    await AudioPlayer.SendSpokenCommandFailureAsync(_player.LastTextChannel, userId,
                        "I couldn't carry out your spoken command. Please try again.", _cancellation.Token);
                }
            }, ReportIssueAsync)))
            {
                overloaded = true;
            }
        });

        try
        {
            await ReadFramesAsync(capture.Stream, frame => ProcessFrameAsync(frame), () => ProcessFrameAsync(null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.DebugLog($"Voice capture failed for user {userId}: {ex}", guildId: _player.Guild.Id);
            await ReportIssueAsync("Voice capture failed for a participant. Rejoining the voice channel can restart it.");
        }
        finally
        {
            lock (_gate)
            {
                if (_captures.TryGetValue(userId, out Capture? current) && ReferenceEquals(current, capture))
                {
                    _captures.Remove(userId);
                }

                capture.Cancellation.Cancel();
                capture.Cancellation.Dispose();
            }
        }

        async Task ProcessFrameAsync(RTPFrame? frame)
        {
            if (frame is { } received)
            {
                segmenter.AddPcm(received.Payload, Stopwatch.GetTimestamp());
            }
            else
            {
                segmenter.FlushIfIdle(Stopwatch.GetTimestamp());
            }

            if (overloaded)
            {
                overloaded = false;
                await ReportIssueAsync("Voice recognition is overloaded; a speech segment was discarded. Please repeat your command.");
            }
        }
    }

    internal static bool IsRepeatedWindowCommand(SpeechSegment segment, SpokenAudioCommand command,
        SpokenAudioCommand? previous, long previousEnd) =>
        segment.IsContinuation && segment.StartSample < previousEnd && previous is not null &&
        command.Action == previous.Action && command.Count == previous.Count && ReferenceEquals(command.TargetSource, previous.TargetSource) &&
        string.Equals(command.Query, previous.Query, StringComparison.OrdinalIgnoreCase);

    internal static async Task ReadFramesAsync(AudioInStream stream, Func<RTPFrame, Task> onFrame,
        Func<Task> onIdle, CancellationToken cancellationToken)
    {
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<RTPFrame>? pending = null;

        try
        {
            // Frames queued before this reader starts may predate model readiness and the listening notice.
            int buffered = stream.AvailableFrames;

            for (int i = 0; i < buffered && stream.TryReadFrame(reads.Token, out _); i++)
            {
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending ??= stream.ReadFrameAsync(reads.Token);
                RTPFrame frame;

                try
                {
                    frame = await pending.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                }
                catch (TimeoutException)
                {
                    await onIdle();
                    continue;
                }

                pending = null;
                await onFrame(frame);
            }
        }
        finally
        {
            await reads.CancelAsync();

            if (pending is not null)
            {
                try
                {
                    await pending;
                }
                catch (OperationCanceledException) when (reads.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (reads.IsCancellationRequested) { }
            }
        }
    }

    private async Task ReportIssueAsync(string message)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastIssue);

        if ((previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(30)) ||
            Interlocked.CompareExchange(ref _lastIssue, now, previous) != previous)
        {
            return;
        }

        _logger.DebugLog(message, guildId: _player.Guild.Id, channelId: _player.VoiceChannel.Id);

        try
        {
            await _player.LastTextChannel.SendMessageAsync(message, allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = _cancellation.Token, Timeout = 10_000 });
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.DebugLog($"Could not report voice recognition failure: {ex}", guildId: _player.Guild.Id);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= Task.Run(DisposeCoreAsync);
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _client.StreamCreated -= _streamCreated;
        _client.StreamDestroyed -= _streamDestroyed;
        _cancellation.Cancel();
        Task[] tasks;

        lock (_gate)
        {
            tasks = _tasks.ToArray();
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            _cancellation.Dispose();
        }
    }
}
