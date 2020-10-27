using Discord.WebSocket;
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
        private readonly TwitchClient _client = new TwitchClient();
        private readonly Logger _logger;
        private readonly DiscordSocketClient _discord;
        private readonly StreamerSongListClient _songListClient;
        private readonly Channel<ChatMessage> _messageChannel =
            Channel.CreateUnbounded<ChatMessage>(new UnboundedChannelOptions() { SingleReader = true });

        public TwitchBotService(Logger logger, DiscordSocketClient discord, StreamerSongListClient songListClient)
        {
            _logger = logger;
            _discord = discord;
            _songListClient = songListClient;

            ConnectionCredentials credentials = new ConnectionCredentials(Secrets.Twitch.Username, Secrets.Twitch.AccessToken);
            _client.Initialize(credentials, Secrets.Twitch.ChannelName);

            _client.OnMessageReceived += (s, e) => _messageChannel.Writer.TryWrite(e.ChatMessage);
            _client.OnJoinedChannel += (s, e) =>
            {
                if (e.Channel.Equals(Secrets.Twitch.ChannelName, StringComparison.OrdinalIgnoreCase))
                {
                    _client.SendMessage(e.Channel, "Beep boop darlBoop");
                }
            };
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
                            await _discord.GetTextChannel(Guilds.Mihu, Channels.TwitchAddLogs).SendMessageAsync(toAdd);
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
                            _client.SendMessage(message.Channel, "@Goldenqt");
                        }
                        else if (text.Contains("salmon", StringComparison.OrdinalIgnoreCase))
                        {
                            _client.SendMessage(message.Channel, "@xJoster");
                        }
                        else if (text.Contains("penis", StringComparison.OrdinalIgnoreCase))
                        {
                            _client.SendMessage(message.Channel, "YEP");
                        }
                        else if (text.StartsWith("!merch", StringComparison.OrdinalIgnoreCase))
                        {
                            _client.SendMessage(message.Channel, "https://streamlabs.com/darleeng/merch");
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync(ex.ToString());
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
            Task.Run(ChannelReaderTaskAsync);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _client.Disconnect();
            return Task.CompletedTask;
        }
    }
}
