using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MihuBot.Helpers;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TwitchLib.Client;
using TwitchLib.Client.Models;

namespace MihuBot
{
    public sealed class TwitchBotService : IHostedService
    {
        private readonly TwitchClient _client = new();
        private readonly Logger _logger;
        private readonly DiscordSocketClient _discord;
        private readonly StreamerSongListClient _songListClient;
        private readonly Channel<ChatMessage> _messageChannel =
            Channel.CreateUnbounded<ChatMessage>(new UnboundedChannelOptions() { SingleReader = true });

        public TwitchBotService(Logger logger, DiscordSocketClient discord, StreamerSongListClient songListClient, IConfiguration configuration)
        {
            _logger = logger;
            _discord = discord;
            _songListClient = songListClient;

#if DEBUG
            const string ChannelName = "MihaZupan";
#else
            const string ChannelName = "Darleeng";
#endif

            ConnectionCredentials credentials = new("MihaZupan", configuration["Twitch:AccessToken"]);
            _client.Initialize(credentials, ChannelName);

            _client.OnMessageReceived += (s, e) => _messageChannel.Writer.TryWrite(e.ChatMessage);
            _client.OnJoinedChannel += (s, e) =>
            {
                if (e.Channel.Equals(ChannelName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.DebugLog($"Joined TwitchChannel {e.Channel}");
                    //_client.SendMessage(e.Channel, "Beep boop darlBoop");
                }
            };
            _client.OnDisconnected += (s, e) =>
            {
                Task.Run(async () =>
                {
                    Exception lastEx = null;
                    for (int i = 0; i < 4; i++)
                    {
                        try
                        {
                            _client.Connect();
                            return;
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, i)));
                        }
                    }

                    await _logger.DebugAsync($"Failed to reconnect: {lastEx}");
                });
            };

            _client.OnSendReceiveData += (s, e) => _logger.DebugLog($"{e.Direction} {e.Data}");
        }

        private async Task ChannelReaderTaskAsync()
        {
            while (await _messageChannel.Reader.WaitToReadAsync())
            {
                while (_messageChannel.Reader.TryRead(out ChatMessage message))
                {
                    try
                    {
                        string text = message.Message;
                        if ((message.IsBroadcaster || message.IsModerator) &&
                            text.StartsWith("!add ", StringComparison.OrdinalIgnoreCase))
                        {
                            string toAdd = text.Substring(5).Trim();
                            _logger.DebugLog($"Twitch: Adding {toAdd} to the list. Requested by {message.Username}");
                            await _discord.GetTextChannel(Channels.TwitchAddLogs).SendMessageAsync(toAdd);
                            _client.SendMessage(message.Channel, $"{toAdd} darlG");

                            int byIndex = toAdd.IndexOf(" by ", StringComparison.OrdinalIgnoreCase);
                            if (byIndex != -1)
                            {
                                string title = toAdd.Substring(0, byIndex).Trim();
                                string artist = toAdd.Substring(byIndex + 4).Trim();
                                await _songListClient.TryAddSongAsync(title, artist);
                            }
                        }
                        else if (text.Contains("stinky", StringComparison.OrdinalIgnoreCase))
                        {
                            if (text.Equals("stinky", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains(" stinky ", StringComparison.OrdinalIgnoreCase) ||
                                text.Equals(" stinky", StringComparison.OrdinalIgnoreCase) ||
                                text.Equals("stinky ", StringComparison.OrdinalIgnoreCase))
                            {
                                _client.SendMessage(message.Channel, "@Goldenqt");
                            }
                        }
                        else if (text.Contains("salmon", StringComparison.OrdinalIgnoreCase))
                        {
                            _client.SendMessage(message.Channel, "@xJoster");
                        }
                        else if (text.Contains("penis", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("cock", StringComparison.OrdinalIgnoreCase))
                        {
                            _client.SendMessage(message.Channel, "YEPP");
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync($"{ex} for {message.Message} in {message.Channel} from {message.Username}");
                    }
                    finally
                    {
                        _logger.DebugLog(message.RawIrcMessage);
                    }
                }
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _client.Connect();
            _ = Task.Run(ChannelReaderTaskAsync, CancellationToken.None);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _client.Disconnect();
            _messageChannel.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }
}
