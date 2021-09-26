using Discord.Audio;
using Discord.Rest;
using Google.Apis.YouTube.v3;
using Newtonsoft.Json;
using SpotifyAPI.Web;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace MihuBot.Audio
{
    public static class GlobalAudioSettings
    {
        public static int StreamBufferMs = 1000;
        public static int PacketLoss = 0;
        public static int MinBitrateKb = 96;
        public static int MinBitrate => 96 * 1000;
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
                        else if (command == "skip") await audioPlayer.MoveNextAsync();
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
                            await audioPlayer.DebugDumpAsync(sb);
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
                            await audioPlayer.EnqueueAsync(new YoutubeAudioSource(ctx.Author, video));
                            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                        }
                        else if (YoutubeHelper.TryParsePlaylistId(argument, out string playlistId))
                        {
                            List<IVideo> videos = await YoutubeHelper.GetVideosAsync(playlistId, _youtubeService);
                            foreach (IVideo video in videos)
                            {
                                await audioPlayer.EnqueueAsync(new YoutubeAudioSource(ctx.Author, video));
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
                                    await audioPlayer.EnqueueAsync(new YoutubeAudioSource(ctx.Author, video));

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
                                await audioPlayer.EnqueueAsync(new YoutubeAudioSource(ctx.Author, video));
                                await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                            }
                        }
                        else if (await YoutubeHelper.TrySearchAsync(ctx.ArgumentString.Replace('-', ' '), _youtubeService) is { } video)
                        {
                            await audioPlayer.EnqueueAsync(new YoutubeAudioSource(ctx.Author, video));
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

    public sealed class AudioPlayer : IAsyncDisposable
    {
        public DiscordSocketClient Client { get; }
        public SocketGuild Guild { get; }
        public SocketVoiceChannel VoiceChannel { get; private set; }
        public GuildAudioSettings AudioSettings { get; private set; }

        public SocketTextChannel LastTextChannel { get; set; }

        public int QueueLength => _audioSources.Count;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Queue<IAudioSource> _audioSources = new();
        private readonly CancellationTokenSource _disposedCts = new();
        private readonly ConcurrentDictionary<ulong, AudioPlayer> _audioPlayers;
        private readonly Logger _logger;
        private RestUserMessage _lastSentStatusMessage;
        private bool _disposed;
        private TaskCompletionSource _pausedCts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IAudioClient _audioClient;
        private IAudioSource _currentAudioSource;
        private AudioOutStream _pcmStream;

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
                VoiceChannel = voiceChannel;
                _audioClient = await voiceChannel.ConnectAsync(selfDeaf: true);

                int bitrate = Math.Max(voiceChannel.Bitrate, GlobalAudioSettings.MinBitrate);
                int bufferMs = GlobalAudioSettings.StreamBufferMs;
                int packetLoss = GlobalAudioSettings.PacketLoss;

                _pcmStream = _audioClient.CreatePCMStream(AudioApplication.Music, bitrate, bufferMs, packetLoss);

                _ = Task.Run(CopyAudioAsync);
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync(ex.ToString());
                await DisposeAsync();
            }
        }

        public bool Unpause()
        {
            return Interlocked.Exchange(ref _pausedCts, null)?.TrySetResult() ?? false;
        }

        public bool Pause()
        {
            if (_pausedCts is null)
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return Interlocked.CompareExchange(ref _pausedCts, tcs, null) is null;
            }
            return false;
        }

        private async Task<bool> TryInit(IAudioSource audioSource)
        {
            try
            {
                int bitrate = VoiceChannel.Bitrate;
                if (bitrate % 1000 == 0) bitrate /= 1000;
                else if (bitrate % 1024 == 0) bitrate /= 1024;

                bitrate = Math.Max(bitrate, GlobalAudioSettings.MinBitrateKb);

                await audioSource.InitializeAsync(bitrate);
                return true;
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync(ex.ToString());
                try
                {
                    await audioSource.DisposeAsync();
                }
                catch { }
                return false;
            }
        }

        private async Task SendCurrentlyPlayingAsync()
        {
            if (_currentAudioSource is IAudioSource audioSource)
            {
                try
                {
                    RestUserMessage lastSentMessage = _lastSentStatusMessage;

                    string escapedTitle = audioSource.Description
                        .Replace("[", "\\[", StringComparison.Ordinal)
                        .Replace("]", "\\]", StringComparison.Ordinal);

                    Embed embed = new EmbedBuilder()
                        .WithTitle("**Now playing**")
                        .WithDescription($"[{escapedTitle}]({audioSource.Url})" +
                            $"\nRequested by {MentionUtils.MentionUser(audioSource.Requester.Id)}" +
                            (QueueLength > 0 ? $"\nSongs in queue: {QueueLength}" : ""))
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
                catch { }
            }
        }

        public async Task EnqueueAsync(IAudioSource audioSource)
        {
            await _lock.WaitAsync();
            try
            {
                if (_currentAudioSource is null)
                {
                    if (await TryInit(audioSource))
                    {
                        _currentAudioSource = audioSource;
                        Unpause();
                        await SendCurrentlyPlayingAsync();
                    }
                }
                else
                {
                    _audioSources.Enqueue(audioSource);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task MoveNextAsync(IAudioSource previous = null)
        {
            await _lock.WaitAsync();
            try
            {
                IAudioSource current = _currentAudioSource;
                if (previous is not null && !ReferenceEquals(current, previous))
                {
                    return;
                }

                IAudioSource next = null;
                while (_audioSources.TryDequeue(out next))
                {
                    if (await TryInit(next))
                    {
                        break;
                    }
                }

                _currentAudioSource = next;
                if (next is not null)
                {
                    Unpause();
                    await SendCurrentlyPlayingAsync();
                }

                if (current is not null)
                {
                    try
                    {
                        await current.DisposeAsync();
                    }
                    catch { }
                }
            }
            catch
            {
                await DisposeAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task CopyAudioAsync()
        {
            const int Channels = 2;
            const int SampleRateMs = 48;
            const int BytesPerSample = 2;
            const int BufferMilliseconds = 100;
            Memory<byte> buffer = new byte[Channels * SampleRateMs * BytesPerSample * BufferMilliseconds];

            while (!_disposedCts.IsCancellationRequested)
            {
                if (_pausedCts is TaskCompletionSource pausedCts)
                {
                    await pausedCts.Task;
                    Interlocked.CompareExchange(ref _pausedCts, null, pausedCts);
                    continue;
                }

                IAudioSource currentSource = Volatile.Read(ref _currentAudioSource);
                if (currentSource is null)
                {
                    continue;
                }

                int read = 0;
                try
                {
                    read = await currentSource.ReadAsync(buffer, _disposedCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.DebugLog(ex.ToString(), guildId: Guild.Id, channelId: VoiceChannel.Id);
                }

                if (read <= 0)
                {
                    await MoveNextAsync(currentSource);
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
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _disposedCts.Cancel();
            Unpause();
            _audioPlayers.TryRemove(new KeyValuePair<ulong, AudioPlayer>(Guild.Id, this));

            await _lock.WaitAsync();
            try
            {
                while (_audioSources.TryDequeue(out IAudioSource audioSource))
                {
                    try
                    {
                        await audioSource.DisposeAsync();
                    }
                    catch { }
                }

                if (Interlocked.Exchange(ref _currentAudioSource, null) is IAudioSource currentSource)
                {
                    try
                    {
                        await currentSource.DisposeAsync();
                    }
                    catch { }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task DebugDumpAsync(StringBuilder sb)
        {
            await _lock.WaitAsync();
            try
            {
                Property(sb, "VoiceChannel", VoiceChannel.Name);
                Property(sb, "VC Bitrate", VoiceChannel.Bitrate.ToString());
                Property(sb, "Volume", AudioSettings.Volume.ToString());
                Property(sb, "QueueLength", QueueLength.ToString());

                if (_currentAudioSource is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine("Current audio source:");
                    AudioSource(sb, _currentAudioSource);
                }

                foreach (IAudioSource audioSource in _audioSources)
                {
                    sb.AppendLine();
                    AudioSource(sb, audioSource);
                }
            }
            finally
            {
                _lock.Release();
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

            if (_currentAudioSource is IAudioSource currentSource)
            {
                string escapedTitle = currentSource.Description
                    .Replace("[", "\\[", StringComparison.Ordinal)
                    .Replace("]", "\\]", StringComparison.Ordinal);

                builder.WithDescription($"Now playing [{escapedTitle}]({currentSource.Url}) (requested by {MentionUtils.MentionUser(currentSource.Requester.Id)})");
                builder.WithThumbnailUrl(currentSource.ThumbnailUrl);
            }

            await _lock.WaitAsync();
            try
            {
                int count = 0;
                foreach (IAudioSource audioSource in _audioSources.Take(25))
                {
                    count++;

                    string escapedTitle = audioSource.Description
                        .Replace("[", "\\[", StringComparison.Ordinal)
                        .Replace("]", "\\]", StringComparison.Ordinal);

                    builder.AddField($"{count}. [{escapedTitle}]({audioSource.Url})", $"Requested by {MentionUtils.MentionUser(audioSource.Requester.Id)}", inline: true);
                }
            }
            finally
            {
                _lock.Release();
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
        Task InitializeAsync(int bitrateHintKbit);

        TimeSpan? Remaining { get; }

        string Description { get; }

        string Url { get; }

        string ThumbnailUrl { get; }

        SocketGuildUser Requester { get; }

        ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);

        void DebugDump(StringBuilder sb) { }
    }

    public sealed class YoutubeAudioSource : IAudioSource
    {
        public SocketGuildUser Requester { get; }

        private readonly IVideo _video;
        private Stream _ffmpegOutputStream;
        private Process _process;
        private CancellationTokenSource _cts;
        private string _temporaryFile;

        public YoutubeAudioSource(SocketGuildUser requester, IVideo video)
        {
            Requester = requester;
            _video = video;
        }

        public async Task InitializeAsync(int bitrateHintKbit)
        {
            _cts = new CancellationTokenSource();

            string cachedPath = await YoutubeAudioCache.GetOrTryCacheAsync(_video, _cts.Token);
            string audioPath = cachedPath ?? (Path.GetTempFileName() + ".opus");
            if (cachedPath is null)
            {
                try
                {
                    StreamManifest manifest = await YoutubeHelper.Streams.GetManifestAsync(_video.Id, _cts.Token);
                    IStreamInfo bestAudio = YoutubeHelper.GetBestAudio(manifest, out _);
                    await YoutubeHelper.ConvertToAudioOutputAsync(bestAudio.Url, audioPath, bitrateHintKbit, _cts.Token);
                }
                catch
                {
                    File.Delete(audioPath);
                    throw;
                }
                _temporaryFile = audioPath;
            }

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

        public TimeSpan? Remaining => null;

        public string Description => _video.Title;

        public string Url => _video.Url;

        public string ThumbnailUrl => _video.Thumbnails.OrderBy(t => t.Resolution.Area).FirstOrDefault()?.Url;

        public ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken)
        {
            return _ffmpegOutputStream.ReadAsync(pcmBuffer, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _cts?.Cancel();
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

            return default;
        }

        public void DebugDump(StringBuilder sb)
        {
            sb.AppendLine($"TemporaryFile: {_temporaryFile ?? "N/A"}");
            sb.AppendLine($"FFmpeg arguments: {_process?.StartInfo.Arguments ?? "N/A"}");
        }
    }

    public static class YoutubeAudioCache
    {
        private sealed class VideoMetadata : IVideo
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
        private static readonly Dictionary<string, Task<string>> _cacheOperations = new();

        public static readonly TimeSpan MaxVideoLength = TimeSpan.FromMinutes(7);

        private static (string Directory, string Path, string MetadataPath) GetFileDirectory(string videoId)
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(videoId)));
            string directory = $"{CacheDirectory}/{key[0]}{key[1]}/";
            string path = $"{directory}{key}{OpusExtension}";
            string metadata = $"{directory}{key}{MetadataExtension}";
            return (directory, path, metadata);
        }

        public static async Task<string> GetOrTryCacheAsync(IVideo video, CancellationToken cancellationToken)
        {
            var (directory, path, metadataPath) = GetFileDirectory(video.Id);

            if (File.Exists(metadataPath))
            {
                try
                {
                    var metadata = JsonConvert.DeserializeObject<VideoMetadata>(await File.ReadAllTextAsync(metadataPath));
                    metadata.LastAccessedAt = DateTime.UtcNow;
                    await File.WriteAllTextAsync(metadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));
                    return metadata.OpusPath;
                }
                catch
                {
                    return null;
                }
            }

            TaskCompletionSource<string> tcs = null;
            Task<string> task;
            lock (_cacheOperations)
            {
                if (!_cacheOperations.TryGetValue(video.Id, out task))
                {
                    tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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

                        if (video.Duration > MaxVideoLength)
                        {
                            metadata.OpusPath = null;
                        }
                        else
                        {
                            StreamManifest streamManifest = await YoutubeHelper.Streams.GetManifestAsync(video.Id);
                            IStreamInfo bestAudio = YoutubeHelper.GetBestAudio(streamManifest, out _);
                            await YoutubeHelper.ConvertToAudioOutputAsync(bestAudio.Url, path, bitrate: 160);
                        }

                        metadata.LastAccessedAt = DateTime.UtcNow;
                        await File.WriteAllTextAsync(metadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));
                        tcs.TrySetResult(metadata.OpusPath);
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
