using Discord.Audio;
using Discord.Rest;
using Google.Apis.YouTube.v3;
using Newtonsoft.Json;
using SpotifyAPI.Web;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace MihuBot.Audio
{
    public static class GlobalAudioSettings
    {
        public static int StreamBufferMs = 1000;
        public static int PacketLoss = 5;
        public static int MinBitrateKb = 96;
        public static int MinBitrate => MinBitrateKb * 1000;
    }

    public sealed class AudioCommands : CommandBase
    {
        private static readonly string[] AvailableCommands = new[] { "p", "play", "pause", "unpause", "resume", "skip", "volume", "queue" };

        public override string Command => "mplay";
        public override string[] Aliases => AvailableCommands.Concat(new[] { "audiocommands", "audiodebug", "audiotempsettings" }).ToArray();

        private readonly AudioService _audioService;
        private readonly SpotifyClient _spotifyClient;
        private readonly YouTubeService _youtubeService;

        public AudioCommands(AudioService audioService, SpotifyClient spotifyClient, YouTubeService youtubeService)
        {
            _audioService = audioService;
            _spotifyClient = spotifyClient;
            _youtubeService = youtubeService;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            ChannelPermissions permissions = ctx.ChannelPermissions;

            _audioService.TryGetAudioPlayer(ctx.Guild.Id, out AudioPlayer audioPlayer);

            if (ctx.Command == "audiotempsettings")
            {
                if (await ctx.RequirePermissionAsync(ctx.Command) &&
                    ctx.Arguments.Length == 3 &&
                    int.TryParse(ctx.Arguments[0], out int streamBufferMs) &&
                    int.TryParse(ctx.Arguments[1], out int packetLoss) &&
                    int.TryParse(ctx.Arguments[2], out int bitrateKbit))
                {
                    GlobalAudioSettings.StreamBufferMs = streamBufferMs;
                    GlobalAudioSettings.PacketLoss = packetLoss;
                    GlobalAudioSettings.MinBitrateKb = bitrateKbit;
                }

                return;
            }

            string command = ctx.Command;
            if (ctx.Arguments.Length == 1 && (command == "p" || command == "play") && AvailableCommands.Contains(ctx.Arguments[0], StringComparison.OrdinalIgnoreCase))
            {
                command = ctx.Arguments[0].ToLowerInvariant();
            }

            if (command == "mplay" || command == "audiocommands")
            {
                await ctx.ReplyAsync($"I know of these: {string.Join(", ", AvailableCommands.Select(c => $"`!{c}`"))}");
                return;
            }

            if (command == "pause" || command == "unpause" || command == "resume" ||
                command == "skip" || command == "volume" || command == "audiodebug" ||
                command == "queue")
            {
                if (audioPlayer is not null)
                {
                    if (permissions.SendMessages)
                    {
                        audioPlayer.LastTextChannel = ctx.Channel;
                    }

                    Task reactionTask = null;
                    if (permissions.AddReactions)
                    {
                        reactionTask = ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }

                    try
                    {
                        if (command == "pause") audioPlayer.Pause();
                        else if (command == "unpause" || command == "resume") audioPlayer.Unpause();
                        else if (command == "skip") await audioPlayer.SkipAsync();
                        else if (command == "volume")
                        {
                            if (ctx.Arguments.Length > 0 && uint.TryParse(ctx.Arguments[0].TrimEnd('%'), out uint volume) && volume > 0 && volume <= 100)
                            {
                                await audioPlayer.AudioSettings.ModifyAsync((settings, volume) => settings.Volume = volume, volume / 100f);
                            }
                            else
                            {
                                await ctx.ReplyAsync("Please specify a volume like `!volume 50`", mention: true);
                            }
                        }
                        else if (command == "queue")
                        {
                            if (permissions.SendMessages)
                            {
                                await audioPlayer.PostQueueAsync();
                            }
                        }
                        else if (command == "audiodebug" && await ctx.RequirePermissionAsync(command))
                        {
                            var sb = new StringBuilder();
                            audioPlayer.DebugDump(sb);
                            await ctx.Channel.SendTextFileAsync($"AudioDebug-{ctx.Guild.Id}.txt", sb.ToString());
                        }
                    }
                    finally
                    {
                        await reactionTask;
                    }
                }
            }
            else
            {
                if (audioPlayer is null)
                {
                    if (!permissions.SendMessages)
                    {
                        if (permissions.AddReactions)
                        {
                            await ctx.Message.AddReactionAsync(Emotes.RedCross);
                        }
                        return;
                    }

                    SocketVoiceChannel voiceChannel = ctx.Guild.VoiceChannels.FirstOrDefault(vc => vc.GetUser(ctx.AuthorId) is not null);

                    if (voiceChannel is null)
                    {
                        await ctx.ReplyAsync("Join a voice channel first");
                        return;
                    }

                    audioPlayer = await _audioService.GetOrCreateAudioPlayerAsync(ctx.Discord, ctx.Channel, voiceChannel);

                    if (permissions.SendMessages)
                    {
                        audioPlayer.LastTextChannel = ctx.Channel;
                    }
                }

                if (ctx.Arguments.Length > 0)
                {
                    string argument = ctx.Arguments[0];
                    try
                    {
                        if (YoutubeHelper.TryParseVideoId(argument, out string videoId))
                        {
                            Video video = await YoutubeHelper.GetVideoAsync(videoId);
                            audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                        }
                        else if (YoutubeHelper.TryParsePlaylistId(argument, out string playlistId))
                        {
                            List<IVideo> videos = await YoutubeHelper.GetVideosAsync(playlistId, _youtubeService);
                            foreach (IVideo video in videos)
                            {
                                audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                            }
                            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                        }
                        else if (TryParseSpotifyPlaylistId(argument, out playlistId))
                        {
                            var page = await _spotifyClient.Playlists.GetItems(playlistId);

                            bool foundAny = false;

                            await foreach (FullTrack track in _spotifyClient.Paginate(page)
                                .Select(t => t.Track)
                                .OfType<FullTrack>())
                            {
                                IVideo searchResult = await YoutubeHelper.TryFindSongAsync(track.Name, track.Artists.FirstOrDefault()?.Name, _youtubeService);
                                if (searchResult is not null)
                                {
                                    Video video = await YoutubeHelper.GetVideoAsync(searchResult.Id);
                                    audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));

                                    if (!foundAny)
                                    {
                                        foundAny = true;
                                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                                    }
                                }
                            }
                        }
                        else if (TryParseSpotifyTrackId(argument, out string trackId))
                        {
                            FullTrack track = await _spotifyClient.Tracks.Get(trackId);
                            IVideo searchResult = await YoutubeHelper.TryFindSongAsync(track.Name, track.Artists.FirstOrDefault()?.Name, _youtubeService);
                            if (searchResult is not null)
                            {
                                Video video = await YoutubeHelper.GetVideoAsync(searchResult.Id);
                                audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                                await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                            }
                        }
                        else if (await YoutubeHelper.TrySearchAsync(ctx.ArgumentString.Replace('-', ' '), _youtubeService) is { } video)
                        {
                            audioPlayer.Enqueue(new YoutubeAudioSource(ctx.Author, video));
                            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                        }
                        else
                        {
                            await ctx.ReplyAsync("Sorry, I don't know that");
                        }
                    }
                    catch (Exception ex)
                    {
                        await ctx.DebugAsync(ex);
                        await ctx.ReplyAsync($"Something went wrong :(\n`{ex.Message}`");
                    }
                }
            }
        }

        private static bool TryParseSpotifyPlaylistId(string argument, out string playlistId)
        {
            playlistId = null;

            if (!argument.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
                !argument.Contains("playlist", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Uri.TryCreate(argument, UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            string path = uri.AbsolutePath;
            if (!path.StartsWith("/playlist/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            playlistId = path.Substring("/playlist/".Length);
            return !playlistId.Contains('/');
        }

        private static bool TryParseSpotifyTrackId(string argument, out string trackId)
        {
            trackId = null;

            if (!argument.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
                !argument.Contains("track", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Uri.TryCreate(argument, UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            string path = uri.AbsolutePath;
            if (!path.StartsWith("/track/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            trackId = path.Substring("/track/".Length);
            return !trackId.Contains('/');
        }
    }

    public sealed class GuildAudioSettings
    {
        [JsonIgnore]
        public SynchronizedLocalJsonStore<Dictionary<ulong, GuildAudioSettings>> UnderlyingStore;

        public float? Volume { get; set; }

        public async Task ModifyAsync<T>(Action<GuildAudioSettings, T> modificationAction, T state)
        {
            await UnderlyingStore.EnterAsync();
            try
            {
                modificationAction(this, state);
            }
            finally
            {
                UnderlyingStore.Exit();
            }
        }
    }

    public class AudioService
    {
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, GuildAudioSettings>> _audioSettings = new("AudioSettings.json", static (store, settings) =>
        {
            foreach (GuildAudioSettings guildSettings in settings.Values)
            {
                guildSettings.UnderlyingStore = store;
            }
            return settings;
        });
        private readonly ConcurrentDictionary<ulong, AudioPlayer> _audioPlayers = new();
        private readonly Logger _logger;

        public AudioService(Logger logger, DiscordSocketClient discord)
        {
            _logger = logger;

            discord.UserVoiceStateUpdated += (user, before, after) =>
            {
                if (user.Id == discord.CurrentUser.Id &&
                    before.VoiceChannel is SocketVoiceChannel vc &&
                    TryGetAudioPlayer(vc.Guild.Id, out AudioPlayer player))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (after.VoiceChannel is null)
                            {
                                await player.DisposeAsync();
                            }
                            else
                            {
                                // Moved between calls?
                                await player.DisposeAsync();

                                await after.VoiceChannel.DisconnectAsync();
                            }
                        }
                        catch { }
                    });
                }
                return Task.CompletedTask;
            };
        }

        public bool TryGetAudioPlayer(ulong guildId, out AudioPlayer audioPlayer)
        {
            return _audioPlayers.TryGetValue(guildId, out audioPlayer);
        }

        public async ValueTask<AudioPlayer> GetOrCreateAudioPlayerAsync(DiscordSocketClient client, SocketTextChannel lastTextChannel, SocketVoiceChannel voiceChannel)
        {
            ulong guildId = voiceChannel.Guild.Id;

            while (true)
            {
                if (TryGetAudioPlayer(guildId, out AudioPlayer audioPlayer))
                {
                    return audioPlayer;
                }

                GuildAudioSettings audioSettings;
                var settings = await _audioSettings.EnterAsync();
                try
                {
                    if (!settings.TryGetValue(guildId, out audioSettings))
                    {
                        audioSettings = settings[guildId] = new()
                        {
                            UnderlyingStore = _audioSettings
                        };
                    }
                }
                finally
                {
                    _audioSettings.Exit();
                }

                audioPlayer = new AudioPlayer(client, audioSettings, _logger, voiceChannel.Guild, lastTextChannel, _audioPlayers);
                if (_audioPlayers.TryAdd(guildId, audioPlayer))
                {
                    await audioPlayer.JoinChannelAsync(voiceChannel);
                    return audioPlayer;
                }
                else
                {
                    await audioPlayer.DisposeAsync();
                }
            }
        }
    }

    public sealed class AudioScheduler : IAsyncDisposable
    {
        private readonly Queue<IAudioSource> _queue = new();
        private int _bitrateHintKbit = GlobalAudioSettings.MinBitrateKb;

        private IAudioSource _current;
        private TaskCompletionSource _skipTcs;
        private TaskCompletionSource _enqueueTcs;

        public void SetBitrateHint(int bitrateHint)
        {
            if (bitrateHint % 1000 == 0) bitrateHint /= 1000;
            else if (bitrateHint % 1024 == 0) bitrateHint /= 1024;
            bitrateHint = Math.Max(bitrateHint, GlobalAudioSettings.MinBitrateKb);

            _bitrateHintKbit = bitrateHint;
        }

        public int QueueLength => _queue.Count;

        public IAudioSource[] GetQueueSnapshot(int limit, out IAudioSource currentSource)
        {
            lock (_queue)
            {
                currentSource = _current;

                if ((uint)_queue.Count < (uint)limit)
                {
                    return _queue.ToArray();
                }

                return _queue.Take(limit).ToArray();
            }
        }

        public void Enqueue(IAudioSource audioSource)
        {
            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    audioSource.StartInitializing(_bitrateHintKbit);
                }

                _queue.Enqueue(audioSource);

                if (_enqueueTcs is not null)
                {
                    _enqueueTcs.SetResult();
                    _enqueueTcs = null;
                }
            }
        }

        public async Task SkipAsync()
        {
            IAudioSource toDispose = null;

            lock (_queue)
            {
                if (_skipTcs is not null)
                {
                    _skipTcs.SetResult();
                    _skipTcs = null;
                }
                else
                {
                    if (_current is not null)
                    {
                        toDispose = _current;
                        _current = null;
                    }
                    else
                    {
                        if (_queue.TryDequeue(out toDispose))
                        {
                            if (_queue.TryPeek(out IAudioSource nextNext))
                            {
                                nextNext.StartInitializing(_bitrateHintKbit);
                            }
                        }
                    }
                }
            }

            if (toDispose is not null)
            {
                await toDispose.DisposeAsync();
            }
        }

        public void SkipCurrent(IAudioSource sourceToSkip)
        {
            lock (_queue)
            {
                if (ReferenceEquals(_current, sourceToSkip))
                {
                    _current = null;
                }
            }
        }

        public async ValueTask<IAudioSource> GetAudioSourceAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                TaskCompletionSource enqueueTcs = null;
                TaskCompletionSource skipTcs = null;
                IAudioSource candidate = null;

                lock (_queue)
                {
                    candidate = _current;

                    if (candidate is null)
                    {
                        if (_queue.TryDequeue(out candidate))
                        {
                            if (_queue.TryPeek(out IAudioSource nextNext))
                            {
                                nextNext.StartInitializing(_bitrateHintKbit);
                            }
                        }
                        else
                        {
                            _enqueueTcs = enqueueTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                    }

                    if (candidate is not null)
                    {
                        Task<bool> initializedTask = candidate.EnsureInitializedAsync();
                        if (initializedTask.IsCompletedSuccessfully && initializedTask.Result)
                        {
                            _current = candidate;
                            return candidate;
                        }

                        _skipTcs = skipTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                if (enqueueTcs is not null)
                {
                    await enqueueTcs.Task.WaitAsync(cancellationToken);
                    continue;
                }

                try
                {
                    Task<bool> initializedTask = candidate.EnsureInitializedAsync();

                    await Task.WhenAny(initializedTask, skipTcs.Task).WaitAsync(cancellationToken);

                    lock (_queue)
                    {
                        _skipTcs = null;

                        if (!skipTcs.Task.IsCompleted && initializedTask.Result)
                        {
                            _current = candidate;
                            return candidate;
                        }
                    }

                    await candidate.DisposeAsync();
                    continue;
                }
                catch
                {
                    await candidate.DisposeAsync();
                    throw;
                }
            }
        }

        public bool TryPeekCurrent(out IAudioSource audioSource)
        {
            audioSource = _current;
            return audioSource is not null;
        }

        public async ValueTask DisposeAsync()
        {
            IAudioSource[] toDispose;
            lock (_queue)
            {
                if (_current is not null)
                {
                    _queue.Enqueue(_current);
                    _current = null;
                }

                toDispose = _queue.ToArray();
                _queue.Clear();
            }

            foreach (IAudioSource audioSource in toDispose)
            {
                await audioSource.DisposeAsync();
            }
        }
    }

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
        private readonly Queue<float> _copyLoopTimings = new(CopyLoopTimings);

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
                _scheduler.SetBitrateHint(voiceChannel.Bitrate);

                VoiceChannel = voiceChannel;
                _audioClient = await voiceChannel.ConnectAsync(selfDeaf: true);

                int bitrate = Math.Max(voiceChannel.Bitrate, GlobalAudioSettings.MinBitrate);
                int bufferMs = GlobalAudioSettings.StreamBufferMs;
                int packetLoss = GlobalAudioSettings.PacketLoss;

                _pcmStream = _audioClient.CreatePCMStream(AudioApplication.Music, bitrate, bufferMs, packetLoss);

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
            const int Channels = 2;
            const int SampleRateMs = 48;
            const int BytesPerSample = 2;
            const int BufferMilliseconds = 200;
            Memory<byte> buffer = new byte[Channels * SampleRateMs * BytesPerSample * BufferMilliseconds];

            IAudioSource previous = null;
            Task sendCurrentlyPlayingTask = Task.CompletedTask;

            Stopwatch stopwatch = Stopwatch.StartNew();
            TimeSpan lastElapsed = stopwatch.Elapsed;

            while (!_disposedCts.IsCancellationRequested)
            {
                TimeSpan elapsed = stopwatch.Elapsed;
                TimeSpan delta = elapsed - lastElapsed;
                lastElapsed = elapsed;
                lock (_copyLoopTimings)
                {
                    if (_copyLoopTimings.Count == CopyLoopTimings) _copyLoopTimings.Dequeue();
                    _copyLoopTimings.Enqueue((float)delta.TotalMilliseconds);
                }

                if (_pausedTcs is TaskCompletionSource pausedTcs)
                {
                    await pausedTcs.Task;
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

                int read = 0;
                try
                {
                    read = await audioSource.ReadAsync(buffer, _disposedCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.DebugLog(ex.ToString(), guildId: Guild.Id, channelId: VoiceChannel.Id);
                }

                if (read <= 0)
                {
                    _scheduler.SkipCurrent(audioSource);
                    await audioSource.DisposeAsync();
                    continue;
                }

                float rawVolume = AudioSettings.Volume.GetValueOrDefault(Constants.VCDefaultVolume);
                if (rawVolume != 1)
                {
                    float volume = rawVolume * rawVolume;
                    Helpers.Helpers.Multiply(MemoryMarshal.Cast<byte, short>(buffer.Span.Slice(0, read)), volume);
                }

                try
                {
                    await _pcmStream.WriteAsync(buffer.Slice(0, read), _disposedCts.Token);
                }
                catch
                {
                    await DisposeAsync();
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
                foreach (float deltaMs in _copyLoopTimings)
                {
                    sb.AppendLine($"{deltaMs:N1}");
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

    public interface IAudioSource : IAsyncDisposable
    {
        void StartInitializing(int bitrateHintKbit);

        Task<bool> EnsureInitializedAsync();

        TimeSpan? Remaining { get; }

        string Description { get; }

        string Url { get; }

        string ThumbnailUrl { get; }

        SocketGuildUser Requester { get; }

        ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);

        void DebugDump(StringBuilder sb) { }
    }

    public abstract class AudioSource : IAudioSource
    {
        public SocketGuildUser Requester { get; }

        private int _startedInitializing;
        private readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _disposedCts = new();

        public AudioSource(SocketGuildUser requester)
        {
            Requester = requester;
        }

        public void StartInitializing(int bitrateHintKbit)
        {
            Task.Run(async () =>
            {
                if (Interlocked.Exchange(ref _startedInitializing, 1) != 0)
                {
                    return;
                }

                try
                {
                    await InitializeAsync(bitrateHintKbit, _disposedCts.Token);
                    _initialized.SetResult(true);
                }
                catch
                {
                    _initialized.SetResult(false);
                    await DisposeAsync();
                }
            });
        }

        public Task<bool> EnsureInitializedAsync() => _initialized.Task;

        public abstract Task InitializeAsync(int bitrateHintKbit, CancellationToken cancellationToken);

        public virtual ValueTask DisposeAsync()
        {
            _disposedCts.Cancel();
            return default;
        }


        public abstract TimeSpan? Remaining { get; }

        public abstract string Description { get; }

        public abstract string Url { get; }

        public abstract string ThumbnailUrl { get; }

        public abstract ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);
    }

    public sealed class YoutubeAudioSource : AudioSource
    {
        private const int Channels = 2;
        private const int SampleRateMs = 48;
        private const int BytesPerSample = 2;
        private const int BytesPerMs = Channels * SampleRateMs * BytesPerSample;

        private readonly IVideo _video;
        private Stream _ffmpegOutputStream;
        private Process _process;
        private string _temporaryFile;
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
            YoutubeAudioCache.VideoMetadata metadata = await YoutubeAudioCache.GetOrTryCacheAsync(_video, cancellationToken);
            string audioPath = metadata.OpusPath ?? (Path.GetTempFileName() + ".opus");
            if (metadata.OpusPath is null)
            {
                try
                {
                    StreamManifest manifest = await YoutubeHelper.Streams.GetManifestAsync(_video.Id, cancellationToken);
                    IStreamInfo bestAudio = YoutubeHelper.GetBestAudio(manifest, out _);
                    _duration ??= bestAudio.Duration();
                    await YoutubeHelper.ConvertToAudioOutputAsync(bestAudio.Url, audioPath, bitrateHintKbit, cancellationToken);
                }
                catch
                {
                    File.Delete(audioPath);
                    throw;
                }
                _temporaryFile = audioPath;
            }

            _duration ??= metadata.Duration;

            _process = new Process
            {
                StartInfo = new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel warning -i \"{audioPath}\" -ac 2 -f s16le -ar 48000 -")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };

            _process.Start();

            _ffmpegOutputStream = _process.StandardOutput.BaseStream;
        }

        public override TimeSpan? Remaining
        {
            get
            {
                if (_duration is null)
                {
                    return null;
                }

                TimeSpan consumed = TimeSpan.FromMilliseconds(Volatile.Read(ref _bytesRead) / BytesPerMs);
                return _duration.Value.Subtract(consumed);
            }
        }

        public override string Description => _video.Title;

        public override string Url => _video.Url;

        public override string ThumbnailUrl => _video.Thumbnails.OrderBy(t => t.Resolution.Area).FirstOrDefault()?.Url;

        public override ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken)
        {
            ValueTask<int> readTask = _ffmpegOutputStream.ReadAsync(pcmBuffer, cancellationToken);
            if (readTask.IsCompletedSuccessfully)
            {
                int read = readTask.GetAwaiter().GetResult();
                Volatile.Write(ref _bytesRead, _bytesRead + read);
                return new ValueTask<int>(read);
            }

            return ReadAsyncCore(readTask);

            async ValueTask<int> ReadAsyncCore(ValueTask<int> readTask)
            {
                int read = await readTask;
                Volatile.Write(ref _bytesRead, _bytesRead + read);
                return read;
            }
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

            try
            {
                if (_temporaryFile is string temporaryFile)
                {
                    File.Delete(temporaryFile);
                }
            }
            catch { }
        }

        public void DebugDump(StringBuilder sb)
        {
            sb.AppendLine($"TemporaryFile: {_temporaryFile ?? "N/A"}");
            sb.AppendLine($"FFmpeg arguments: {_process?.StartInfo.Arguments ?? "N/A"}");
        }
    }

    public static class YoutubeAudioCache
    {
        public sealed class VideoMetadata : IVideo
        {
            public VideoId Id { get; set; }
            public string Url { get; set; }
            public string Title { get; set; }
            public Author Author { get; set; }
            public TimeSpan? Duration { get; set; }
            public IReadOnlyList<Thumbnail> Thumbnails { get; set; }
            public DateTime DownloadedAt { get; set; }
            public DateTime LastAccessedAt { get; set; }
            public string OpusPath { get; set; }
        }

        private const string OpusExtension = ".opus";
        private const string MetadataExtension = ".metadata";
        private const string CacheDirectory = "YoutubeAudioCache";
        private static readonly Dictionary<string, Task<VideoMetadata>> _cacheOperations = new();

        public static readonly TimeSpan MaxVideoLength = TimeSpan.FromMinutes(7);

        private static (string Directory, string Path, string MetadataPath) GetFileDirectory(string videoId)
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(videoId)));
            string directory = $"{CacheDirectory}/{key[0]}{key[1]}/";
            string path = $"{directory}{key}{OpusExtension}";
            string metadata = $"{directory}{key}{MetadataExtension}";
            return (directory, path, metadata);
        }

        public static async Task<VideoMetadata> GetOrTryCacheAsync(IVideo video, CancellationToken cancellationToken)
        {
            var (directory, path, metadataPath) = GetFileDirectory(video.Id);

            if (File.Exists(metadataPath))
            {
                try
                {
                    var metadata = JsonConvert.DeserializeObject<VideoMetadata>(await File.ReadAllTextAsync(metadataPath));
                    metadata.LastAccessedAt = DateTime.UtcNow;
                    await File.WriteAllTextAsync(metadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));
                    return metadata;
                }
                catch
                {
                    return null;
                }
            }

            TaskCompletionSource<VideoMetadata> tcs = null;
            Task<VideoMetadata> task;
            lock (_cacheOperations)
            {
                if (!_cacheOperations.TryGetValue(video.Id, out task))
                {
                    tcs = new TaskCompletionSource<VideoMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _cacheOperations.Add(video.Id, tcs.Task);
                }
            }

            if (task is null)
            {
                task = tcs.Task;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Directory.CreateDirectory(directory);

                        var metadata = new VideoMetadata
                        {
                            Id = video.Id,
                            Url = video.Url,
                            Title = video.Title,
                            Author = video.Author,
                            Duration = video.Duration,
                            Thumbnails = video.Thumbnails,
                            DownloadedAt = DateTime.UtcNow,
                            LastAccessedAt = DateTime.UtcNow,
                            OpusPath = path
                        };

                        if (metadata.Duration.HasValue && metadata.Duration > MaxVideoLength)
                        {
                            metadata.OpusPath = null;
                        }
                        else
                        {
                            StreamManifest streamManifest = await YoutubeHelper.Streams.GetManifestAsync(video.Id);
                            IStreamInfo bestAudio = YoutubeHelper.GetBestAudio(streamManifest, out _);

                            metadata.Duration ??= bestAudio.Duration();

                            if (metadata.Duration > MaxVideoLength)
                            {
                                metadata.OpusPath = null;
                            }
                            else
                            {
                                await YoutubeHelper.ConvertToAudioOutputAsync(bestAudio.Url, path, bitrate: 160);
                            }
                        }

                        metadata.LastAccessedAt = DateTime.UtcNow;
                        await File.WriteAllTextAsync(metadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));
                        tcs.TrySetResult(metadata);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        lock (_cacheOperations)
                        {
                            _cacheOperations.Remove(video.Id);
                        }
                    }
                }, CancellationToken.None);
            }

            return await task.WaitAsync(cancellationToken);
        }
    }
}
