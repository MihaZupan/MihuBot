using Discord;
using Discord.Audio;
using Discord.Rest;
using Discord.WebSocket;
using MihuBot.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace MihuBot.Audio
{
    public sealed class AudioCommands : CommandBase
    {
        public override string Command => "mplay";
        public override string[] Aliases => new[] { "pause", "unpause", "skip" };

        private readonly AudioService _audioService;

        public AudioCommands(AudioService audioService)
        {
            _audioService = audioService;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            ChannelPermissions permissions = ctx.BotUser.GetPermissions(ctx.Channel);

            _audioService.TryGetAudioPlayer(ctx.Guild.Id, out AudioPlayer audioPlayer);

            if (ctx.Command == "pause" || ctx.Command == "unpause" || ctx.Command == "skip")
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
                        if (ctx.Command == "pause") audioPlayer.Pause();
                        else if (ctx.Command == "unpause") audioPlayer.Unpause();
                        else if (ctx.Command == "skip") await audioPlayer.MoveNextAsync();
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
                }

                if (ctx.Arguments.Length > 0)
                {
                    string argument = ctx.Arguments[0];
                    if (YoutubeHelper.TryParsePlaylistId(argument, out string playlistId))
                    {
                        List<PlaylistVideo> videos = await YoutubeHelper.GetVideosAsync(playlistId);
                        foreach (PlaylistVideo video in videos)
                        {
                            await audioPlayer.EnqueueAsync(new YoutubeAudioSource(ctx.Author, video));
                        }
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    else if (YoutubeHelper.TryParseVideoId(argument, out string videoId))
                    {
                        Video video = await YoutubeHelper.Youtube.Videos.GetAsync(videoId);
                        await audioPlayer.EnqueueAsync(new YoutubeAudioSource(ctx.Author, video));
                        await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
                    }
                    else
                    {
                        await ctx.ReplyAsync("Sorry, I don't know that");
                    }
                }
            }
        }
    }

    public class AudioService
    {
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
                    if (after.VoiceChannel is null)
                    {
                        return player.DisposeAsync().AsTask();
                    }
                    else
                    {
                        // Moved between calls?
                        return player.DisposeAsync().AsTask();
                    }
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

                audioPlayer = new AudioPlayer(client, _logger, voiceChannel.Guild, lastTextChannel, _audioPlayers);
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

        public AudioPlayer(DiscordSocketClient client, Logger logger, SocketGuild guild, SocketTextChannel lastTextChannel, ConcurrentDictionary<ulong, AudioPlayer> audioPlayers)
        {
            Client = client;
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
                _pcmStream = _audioClient.CreatePCMStream(AudioApplication.Music, voiceChannel.Bitrate, bufferMillis: 200, packetLoss: 0);

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
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Interlocked.CompareExchange(ref _pausedCts, tcs, null) is null;
        }

        private async Task<bool> TryInit(IAudioSource audioSource)
        {
            try
            {
                int? bitrate = VoiceChannel.Bitrate;
                if (bitrate % 1000 == 0) bitrate /= 1000;
                else if (bitrate % 1024 == 0) bitrate /= 1024;
                else bitrate = null;

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
                    if (lastSentMessage is not null)
                    {
                        if (lastSentMessage.Channel.Id != LastTextChannel.Id ||
                            DateTime.UtcNow.Subtract(lastSentMessage.Timestamp.UtcDateTime) > TimeSpan.FromMinutes(5))
                        {
                            lastSentMessage = null;
                        }
                    }

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
                            await lastSentMessage.DeleteAsync();
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
                catch { }

                if (read <= 0)
                {
                    await MoveNextAsync(currentSource);
                    continue;
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
    }

    public interface IAudioSource : IAsyncDisposable
    {
        Task InitializeAsync(int? bitrateHint);

        TimeSpan Remaining { get; }

        string Description { get; }

        string Url { get; }

        string ThumbnailUrl { get; }

        SocketGuildUser Requester { get; }

        ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);
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

        public async Task InitializeAsync(int? bitrateHint)
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
                    await YoutubeHelper.ConvertToAudioOutputAsync(bestAudio.Url, audioPath, bitrateHint.GetValueOrDefault(96), _cts.Token);
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
                StartInfo = new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel warning -i \"{audioPath}\" -filter:a \"volume=0.5\" -ac 2 -f s16le -ar 48000 -")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };

            _process.Start();

            _ffmpegOutputStream = _process.StandardOutput.BaseStream;
        }

        public TimeSpan Remaining => throw new NotImplementedException();

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

            if (cancellationToken.CanBeCanceled)
            {
                TaskCompletionSource cancelTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(static s => ((TaskCompletionSource)s).TrySetResult(), cancelTcs))
                {
                    await Task.WhenAny(task, cancelTcs.Task);
                    cancellationToken.ThrowIfCancellationRequested();
                    return await task;
                }
            }
            else
            {
                return await task;
            }
        }
    }
}
