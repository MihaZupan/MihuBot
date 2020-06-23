using Discord;
using Discord.Audio;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MihuBot
{
    public class AudioClient
    {
        private static readonly Dictionary<ulong, AudioClient> _audioClients = new Dictionary<ulong, AudioClient>();


        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly SocketGuild _guild;
        private readonly AudioOutStream _audioStream;

        private readonly Queue<AudioSource> _sourcesQueue;
        private AudioSource _activeStream;
        private IVoiceChannel _voiceChannel;

        private AudioClient(SocketGuild guild, IVoiceChannel voiceChannel)
        {
            _guild = guild;
            _voiceChannel = voiceChannel;
            _audioStream = guild.AudioClient.CreatePCMStream(AudioApplication.Music, voiceChannel.Bitrate);
            _sourcesQueue = new Queue<AudioSource>();
        }

        public static async Task<AudioClient> TryGetOrJoinAsync(SocketGuild guild, SocketVoiceChannel channelToJoin)
        {
            lock (_audioClients)
            {
                if (_audioClients.TryGetValue(guild.Id, out var audioClient))
                {
                    return audioClient;
                }
            }

            if (channelToJoin is null)
                return null;

            await channelToJoin.ConnectAsync();

            lock (_audioClients)
            {
                if (_audioClients.TryGetValue(guild.Id, out var audioClient))
                {
                    return audioClient;
                }

                audioClient = new AudioClient(guild, channelToJoin);
                _audioClients.Add(guild.Id, audioClient);
                return audioClient;
            }
        }

        private static readonly string[] ValidExtensions = new string[]
        {
            ".mp3",
            ".wav",
            ".opus",
            ".flac"
        };

        public async Task TryQueueContentAsync(SocketMessage message)
        {
            Uri uri = null;
            string url = message.Content
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(p => p.Contains("://") && Uri.TryCreate(p, UriKind.Absolute, out uri));

            if (url is null)
            {
                await message.ReplyAsync("Please supply a valid url", mention: true);
                return;
            }

            string extension = Path.GetExtension(uri.AbsolutePath);

            if (ValidExtensions.Contains(extension, StringComparison.OrdinalIgnoreCase))
            {
                AddAudioSource(new DirectAudioUrlSource(uri));
            }
            else
            {
                await message.ReplyAsync($"Source must be: {string.Join(", ", ValidExtensions.Select(ext => "`" + ext + "`"))}");
            }
        }

        private void AddAudioSource(AudioSource source)
        {
            lock (_audioStream)
            {
                if (_activeStream is null)
                {
                    _activeStream = source;
                    _ = Task.Run(async () => await SourceRelayTaskAsync());
                }
                else
                {
                    _sourcesQueue.Enqueue(source);
                }
            }
        }

        private async Task SourceRelayTaskAsync()
        {
            byte[] buffer = new byte[32 * 1024];

            while (true)
            {
                Debug.Assert(_activeStream != null);

                try
                {
                    await _activeStream.InitAsync();

                    int read;
                    while ((read = await _activeStream.ReadAsync(buffer)) > 0)
                    {
                        await _audioStream.WriteAsync(buffer.AsMemory(0, read));
                    }
                }
                catch (Exception ex)
                {
                    await Program.DebugAsync(ex.ToString());
                }

                try
                {
                    await _activeStream.CleanupAsync();
                }
                catch { }

                lock (_sourcesQueue)
                {
                    if (_sourcesQueue.Count == 0)
                    {
                        _activeStream = null;
                        break;
                    }

                    _activeStream = _sourcesQueue.Dequeue();
                    continue;
                }
            }
        }




        public sealed class DirectAudioUrlSource : AudioSource
        {
            private readonly Uri _uri;
            private string _tempFilePath;

            private Stream _tempFileReadStream;

            public DirectAudioUrlSource(Uri uri)
            {
                _uri = uri;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer) => _tempFileReadStream.ReadAsync(buffer);

            public override async Task InitAsync()
            {
                _tempFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Path.GetExtension(_uri.AbsolutePath).ToLowerInvariant());

                var response = await HttpClient.GetAsync(_uri, HttpCompletionOption.ResponseHeadersRead);
                var stream = await response.Content.ReadAsStreamAsync();

                using var fs = File.OpenWrite(_tempFilePath);
                await stream.CopyToAsync(fs);
                await fs.FlushAsync();

                string outputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pcm");

                try
                {
                    ConvertToPcm(_tempFilePath, outputPath);
                }
                finally
                {
                    File.Delete(_tempFilePath);
                    _tempFilePath = outputPath;
                }

                if (!File.Exists(_tempFilePath))
                    throw new FileNotFoundException(null, outputPath);

                if (new FileInfo(_tempFilePath).Length < 4097)
                    throw new Exception("Too short");

                _tempFileReadStream = File.OpenRead(_tempFilePath);
            }

            public static void ConvertToPcm(string sourcePath, string outputPath)
            {
                using Process ffmpeg = new Process();
                ffmpeg.StartInfo.FileName = @"ffmpeg";
                ffmpeg.StartInfo.Arguments = $"-y -hide_banner -loglevel warning -i \"{sourcePath}\" -filter:a \"volume=0.75\" -ac 2 -f s16le -acodec pcm_s16le -vn \"{outputPath}\"";
                ffmpeg.Start();
                ffmpeg.WaitForExit();
            }

            public override Task CleanupAsync()
            {
                try
                {
                    _tempFileReadStream.Dispose();
                }
                catch { }

                File.Delete(_tempFilePath);
                return Task.CompletedTask;
            }
        }

        public abstract class AudioSource
        {
            public abstract Task InitAsync();
            public abstract ValueTask<int> ReadAsync(Memory<byte> buffer);
            public virtual Task CleanupAsync() => Task.CompletedTask;
        }
    }
}
