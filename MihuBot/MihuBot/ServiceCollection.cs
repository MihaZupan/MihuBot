using Discord.WebSocket;
using StackExchange.Redis;
using System.Net.Http;

namespace MihuBot
{
    public sealed class ServiceCollection
    {
        public readonly DiscordSocketClient Discord;
        public readonly HttpClient Http;
        public readonly ConnectionMultiplexer Redis;

        public Logger Logger;

        public ServiceCollection(DiscordSocketClient discord, HttpClient http, ConnectionMultiplexer redis)
        {
            Discord = discord;
            Http = http;
            Redis = redis;
        }
    }
}
