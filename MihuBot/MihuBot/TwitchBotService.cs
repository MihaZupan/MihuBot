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
        private readonly Channel<ChatMessage> _messageChannel =
            Channel.CreateUnbounded<ChatMessage>(new UnboundedChannelOptions() { SingleReader = true });

        public TwitchBotService(Logger logger, DiscordSocketClient discord)
        {
            _logger = logger;
            _discord = discord;

            ConnectionCredentials credentials = new ConnectionCredentials(Secrets.Twitch.Username, Secrets.Twitch.AccessToken);
            _client.Initialize(credentials, Secrets.Twitch.ChannelName);

            _client.OnMessageReceived += (s, e) => _messageChannel.Writer.TryWrite(e.ChatMessage);
        }

        private async Task ChannelReaderTaskAsync()
        {
            while (await _messageChannel.Reader.WaitToReadAsync())
            {
                while (_messageChannel.Reader.TryRead(out ChatMessage message))
                {
                    try
                    {
                        if ((message.IsBroadcaster || message.IsModerator) &&
                            message.Message.StartsWith("!add ", StringComparison.OrdinalIgnoreCase))
                        {
                            string toAdd = message.Message.Substring(5).Trim();
                            _logger.DebugLog($"Twitch: Adding {toAdd} to the list. Requested by {message.Username}");
                            await _discord.GetTextChannel(Guilds.Mihu, Channels.TwitchAddLogs).SendMessageAsync(toAdd);
                            _client.SendMessage(message.Channel, $"{toAdd} darlG");
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync(ex.ToString());
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
