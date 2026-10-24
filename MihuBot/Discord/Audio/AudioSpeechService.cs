using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MihuBot.Configuration;
using MihuBot.Helpers.AI;
using Whisper.net;

#nullable enable

namespace MihuBot.Discord.Audio;

internal interface ILocalSpeechTranscriber : IAsyncDisposable
{
    Task<string> TranscribeAsync(float[] samples, int threads, CancellationToken cancellationToken);
}

internal sealed class WhisperSpeechTranscriber : ILocalSpeechTranscriber
{
    private readonly WhisperFactory _factory;
    private WhisperProcessor? _processor;
    private int _threads;

    public WhisperSpeechTranscriber(string modelPath)
    {
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
    }

    public async Task<string> TranscribeAsync(float[] samples, int threads, CancellationToken cancellationToken)
    {
        if (_processor is null || _threads != threads)
        {
            if (_processor is not null)
            {
                await _processor.DisposeAsync();
                _processor = null;
            }

            _processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(threads)
                .WithNoContext()
                .WithPrompt("Names: MihuBot, Mihu-Bot, Miha Bot, Mihu Bot, Me Who Bot.")
                .Build();
            _threads = threads;
        }

        var text = new StringBuilder();

        await foreach (SegmentData segment in _processor.ProcessAsync(samples, cancellationToken))
        {
            text.Append(segment.Text).Append(' ');
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_processor is not null)
            {
                await _processor.DisposeAsync();
            }
        }
        finally
        {
            _factory.Dispose();
        }
    }
}

internal sealed record AudioSpeechRequest(
    float[] Samples,
    long Timestamp,
    CancellationToken CancellationToken,
    Func<string, CancellationToken, Task> OnTranscript,
    Func<string, Task> OnError);

public sealed class AudioSpeechService : BackgroundService
{
    internal const int QueueCapacity = 256;
    internal static readonly TimeSpan MaxRequestAge = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan MaxCommandDuration = TimeSpan.FromMinutes(2);

    private readonly Channel<AudioSpeechRequest> _queue = Channel.CreateBounded<AudioSpeechRequest>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Lock _queueGate = new();
    private long _queuedSamples;
    private readonly Func<CancellationToken, Task<ILocalSpeechTranscriber>> _createTranscriber;
    private readonly Func<int> _getThreads;
    private readonly Func<ulong, IChatClient>? _createChat;
    private readonly SpokenSongResolver? _songs;
    private readonly Action<Exception> _logError;
    private readonly Action<ulong, string>? _logCloudRequest;
    private readonly Action<string>? _logWhisperProcessing;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _initializationError;

    public AudioSpeechService(IConfigurationService configuration, HttpClient http, Logger logger, OpenAIService openAI, SpokenSongResolver songs)
        : this(async cancellationToken =>
        {
            string path = WhisperModel.ModelPath;

            if (!File.Exists(path))
            {
                logger.DebugLog("Downloading the local Whisper speech model.");
            }

            await WhisperModel.EnsureDownloadedAsync(http, path, WhisperModel.DownloadUri, WhisperModel.Sha512, cancellationToken);
            return new WhisperSpeechTranscriber(path);
        },
            ex => logger.DebugLog($"Local voice recognition failed: {ex}"),
            () => GetThreads(configuration),
            guildId => openAI.GetResponsesChat(guildId), songs,
            (guildId, message) => logger.DebugLog(message, guildId: guildId),
            message => logger.DebugLog(message))
    {
    }

    internal AudioSpeechService(Func<CancellationToken, Task<ILocalSpeechTranscriber>> createTranscriber, Action<Exception> logError,
        Func<int>? getThreads = null, Func<ulong, IChatClient>? createChat = null, SpokenSongResolver? songs = null,
        Action<ulong, string>? logCloudRequest = null, Action<string>? logWhisperProcessing = null, TimeProvider? timeProvider = null)
    {
        _createTranscriber = createTranscriber;
        _logError = logError;
        _getThreads = getThreads ?? (() => Math.Min(8, Environment.ProcessorCount));
        _createChat = createChat;
        _songs = songs;
        _logCloudRequest = logCloudRequest;
        _logWhisperProcessing = logWhisperProcessing;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static int GetThreads(IConfigurationService configuration)
    {
        int threads = configuration.GetInt32(null, "AudioSpeech.Threads", Math.Min(8, Environment.ProcessorCount));
        return threads > 0
            ? threads
            : throw new InvalidOperationException("Global runtime setting AudioSpeech.Threads must be a positive integer.");
    }

    internal async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!await _ready.Task.WaitAsync(cancellationToken))
        {
            throw new InvalidOperationException("Local voice recognition is unavailable.", _initializationError);
        }
    }

    internal bool TryEnqueue(AudioSpeechRequest request)
    {
        lock (_queueGate)
        {
            if (!_ready.Task.IsCompletedSuccessfully || !_ready.Task.Result || !_queue.Writer.TryWrite(request))
            {
                return false;
            }

            _queuedSamples += request.Samples.Length;
            return true;
        }
    }

    internal async Task<SpokenAudioCommand?> ParseCommandAsync(ulong guildId, string transcript, CancellationToken cancellationToken,
        IReadOnlyList<IAudioSource>? tracks = null)
    {
        if (SpokenAudioCommand.GetAddressedText(transcript) is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (SpokenAudioCommand.ParseLocalCommand(transcript) is { } localCommand)
        {
            return localCommand;
        }

        using IChatClient chat = _createChat?.Invoke(guildId)
            ?? throw new InvalidOperationException("Spoken music commands require Azure OpenAI for intent extraction.");
        return await SpokenAudioCommand.ParseAsync(chat, transcript, cancellationToken, tracks,
            message => _logCloudRequest?.Invoke(guildId, message));
    }

    internal Task<SpokenSongResolution> ResolveSongAsync(ulong guildId, SpokenAudioCommand command, CancellationToken cancellationToken) =>
        (_songs ?? throw new InvalidOperationException("Spoken song resolution is unavailable.")).ResolveAsync(guildId, command, cancellationToken);

    internal async Task<SpokenAudioToolResult> RunToolsAsync(ulong guildId, string request, ISpokenAudioPlayer player, Action<string> logApplied, CancellationToken cancellationToken)
    {
        SpokenSongResolver songs = _songs ?? throw new InvalidOperationException("Spoken song search is unavailable.");
        IChatClient chat = _createChat?.Invoke(guildId)
            ?? throw new InvalidOperationException("Spoken playback tools require Azure OpenAI.");
        return await new SpokenAudioTools(player, songs.SearchAsync).RunAsync(chat, request,
            message => _logCloudRequest?.Invoke(guildId, message), logApplied, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ILocalSpeechTranscriber transcriber;

        try
        {
            transcriber = await _createTranscriber(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(stoppingToken);
            _queue.Writer.TryComplete();
            return;
        }
        catch (Exception ex)
        {
            _initializationError = ex;
            _logError(ex);
            _ready.TrySetResult(false);
            _queue.Writer.TryComplete();
            return;
        }

        await using (transcriber)
        {
            _ready.TrySetResult(true);

            try
            {
                await foreach (AudioSpeechRequest request in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    lock (_queueGate)
                    {
                        _queuedSamples -= request.Samples.Length;
                    }

                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, request.CancellationToken);

                    try
                    {
                        TimeSpan age = _timeProvider.GetElapsedTime(request.Timestamp);

                        if (age >= MaxRequestAge)
                        {
                            await ReportFailureAsync(request, "Voice recognition is overloaded; an expired speech segment was discarded. Please repeat your command.");
                            continue;
                        }

                        using var deadline = new CancellationTokenSource(MaxRequestAge - age, _timeProvider);
                        using var deadlineRegistration = deadline.Token.Register(cancellation.Cancel);
                        int threads = _getThreads();
                        long started = _timeProvider.GetTimestamp();
                        bool completed = false;
                        string transcript;

                        try
                        {
                            transcript = await transcriber.TranscribeAsync(request.Samples, threads, cancellation.Token);
                            cancellation.Token.ThrowIfCancellationRequested();
                            completed = true;
                        }
                        finally
                        {
                            TimeSpan processing = _timeProvider.GetElapsedTime(started);
                            double clipSeconds = (double)request.Samples.Length / SpeechSegmenter.SampleRate;
                            int queuedSegments;
                            long queuedSamples;

                            lock (_queueGate)
                            {
                                queuedSegments = _queue.Reader.Count;
                                queuedSamples = _queuedSamples;
                            }

                            _logWhisperProcessing?.Invoke($"[AudioWhisper] {JsonSerializer.Serialize(new
                            {
                                Completed = completed,
                                ClipSeconds = clipSeconds,
                                ProcessingSeconds = processing.TotalSeconds,
                                RealTimeFactor = clipSeconds > 0 ? (double?)(processing.TotalSeconds / clipSeconds) : null,
                                QueueWaitSeconds = age.TotalSeconds,
                                QueuedSegments = queuedSegments,
                                QueuedSeconds = (double)queuedSamples / SpeechSegmenter.SampleRate,
                                Threads = threads
                            })}");
                        }

                        if (_timeProvider.GetElapsedTime(request.Timestamp) <= MaxRequestAge)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            deadline.CancelAfter(MaxCommandDuration);
                            await request.OnTranscript(transcript, cancellation.Token);
                        }
                        else
                        {
                            await ReportFailureAsync(request, "Voice recognition fell behind; please repeat your command.");
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        if (!stoppingToken.IsCancellationRequested && !request.CancellationToken.IsCancellationRequested)
                        {
                            await ReportFailureAsync(request, "Voice recognition timed out; please repeat your command.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logError(ex);
                        await ReportFailureAsync(request, "A spoken command could not be processed. Please try again or use a text command.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally
            {
                lock (_queueGate)
                {
                    _queue.Writer.TryComplete();

                    while (_queue.Reader.TryRead(out _))
                    {
                    }

                    _queuedSamples = 0;
                }
            }
        }
    }

    private async Task ReportFailureAsync(AudioSpeechRequest request, string message)
    {
        if (request.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await request.OnError(message);
        }
        catch (Exception ex)
        {
            _logError(ex);
        }
    }
}
