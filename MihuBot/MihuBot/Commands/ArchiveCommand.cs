using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using TwitchLib.Api;
using TwitchLib.Api.Core;
using TwitchLib.Api.Helix.Models.Streams.GetStreams;
using TwitchLib.Api.Helix.Models.Users.GetUsers;
using TwitchLib.Api.Interfaces;
using TwitchLib.PubSub;
using TwitchLib.PubSub.Events;
using TwitchLib.PubSub.Interfaces;

namespace MihuBot.Commands
{
    public sealed class ArchiveCommand : CommandBase
    {
        public override string Command => "archive";

        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);

        protected override int CooldownToleranceCount => 5;

        private readonly SynchronizedLocalJsonStore<Dictionary<string, string>> _channels = new("TwitchArchive_Channels.json",
            init: d => new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase));
        private readonly object _lock = new();
        private readonly List<ITwitchPubSub> _pubSubs = new();
        private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Logger _logger;
        private readonly ITwitchAPI _twitchApi;
        private readonly BlobContainerClient _blobContainerClient;

        public ArchiveCommand(Logger logger, IConfiguration configuration)
        {
            _blobContainerClient = new BlobContainerClient(
                configuration["AzureStorage:ConnectionString"],
                "archive-twitch");

            _logger = logger;
            _twitchApi = new TwitchAPI(settings: new ApiSettings
            {
                ClientId = configuration["Twitch:ClientId"],
                Secret = configuration["Twitch:Secret"]
            });
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (ctx.Arguments.Length != 1)
            {
                await ctx.ReplyAsync("Usage: `!archive MihuBot");
                return;
            }

            if (!await ctx.RequirePermissionAsync("archive.twitch"))
                return;

            Dictionary<string, string> channels = await _channels.EnterAsync();
            try
            {
                string channel = ctx.Arguments[0];

                if (channels.ContainsKey(channel))
                {
                    await ctx.ReplyAsync("Channel is already being archived");
                    return;
                }

                GetUsersResponse response = await _twitchApi.Helix.Users.GetUsersAsync(logins: new List<string> { channel });

                if (!response.Users.Any())
                {
                    await ctx.ReplyAsync("Could not find that channel");
                    return;
                }

                User user = response.Users[0];
                string name = user.DisplayName;
                string id = user.Id;

                channels.Add(name, id);
                StartListening(name, id);

                await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);
            }
            finally
            {
                _channels.Exit();
            }
        }

        private void OnLog(object sender, OnLogArgs e)
        {
            _logger.DebugLog($"{nameof(ArchiveCommand)}: {e.Data}");
        }

        private async Task<Stream> TryGetStreamAsync(string id)
        {
            GetStreamsResponse response = await _twitchApi.Helix.Streams.GetStreamsAsync(userIds: new List<string> { id });
            return response.Streams.FirstOrDefault();
        }

        private void OnStreamUp(string channel, string id)
        {
            lock (_lock)
            {
                if (!_processes.TryAdd(channel, null))
                {
                    return;
                }
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));

                    DateTime start = DateTime.UtcNow;
                    while (start.AddMinutes(1) < DateTime.UtcNow)
                    {
                        if (await TryGetStreamAsync(id) is not null)
                        {
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }

                    string url = $"https://twitch.tv/{channel}";

                    while (await TryGetStreamAsync(id) is not null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));

                        BlobClient blobClient = _blobContainerClient.GetBlobClient($"{channel}/{DateTime.UtcNow:yyyyMMddHHmmss}.mp4");

                        using var process = new Process()
                        {
                            StartInfo = new ProcessStartInfo("youtube-dl")
                            {
                                Arguments = $"{url} -o -",
                                UseShellExecute = false,
                                RedirectStandardOutput = true
                            }
                        };

                        lock (_lock)
                        {
                            _processes[channel] = process;
                        }

                        process.Start();

                        await _logger.DebugAsync($"Starting archival of <{url}>");

                        await blobClient.UploadAsync(process.StandardOutput.BaseStream, new BlobUploadOptions
                        {
                            AccessTier = AccessTier.Hot
                        });

                        await process.WaitForExitAsync();

                        await _logger.DebugAsync($"Finished archiving <{blobClient.Uri.AbsoluteUri}>");
                    }
                }
                catch (Exception ex)
                {
                    await _logger.DebugAsync(ex.ToString());
                }
                finally
                {
                    lock (_lock)
                    {
                        _processes.Remove(channel);
                    }
                }
            });
        }

        private void StartListening(string name, string id)
        {
            var pubSub = new TwitchPubSub();
            pubSub.OnLog += OnLog;
            pubSub.OnStreamUp += (_, _) => OnStreamUp(name, id);
            pubSub.OnPubSubServiceConnected += (_, _) => pubSub.SendTopics();

            lock (_lock)
            {
                _pubSubs.Add(pubSub);
            }

            pubSub.ListenToVideoPlayback(id);

            pubSub.Connect();

            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        bool alreadyArchiving;
                        lock (_lock)
                        {
                            alreadyArchiving = _processes.ContainsKey(name);
                        }

                        if (!alreadyArchiving && await TryGetStreamAsync(id) is not null)
                        {
                            OnStreamUp(name, id);
                        }
                    }
                    catch
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10));
                    }

                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
            });
        }

        public override async Task InitAsync()
        {
            await Task.Yield();

            KeyValuePair<string, string>[] channels = await _channels.QueryAsync(c => c.ToArray());
            for (int i = 0; i < channels.Length; i++)
            {
                StartListening(channels[i].Key, channels[i].Value);

                if (i != channels.Length)
                    await Task.Delay(1_000);
            }
        }
    }
}
