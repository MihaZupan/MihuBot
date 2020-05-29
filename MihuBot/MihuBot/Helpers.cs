using Discord;
using Discord.WebSocket;
using System;
using System.Threading.Tasks;

namespace MihuBot
{
    public static class Helpers
    {
        public static async Task ReplyAsync(this SocketMessage message, string text, bool mention = false)
        {
            await message.Channel.SendMessageAsync(mention ? string.Concat(MentionUtils.MentionUser(message.Author.Id), " ", text) : text);
        }

        public static bool StartsWith(this ReadOnlySpan<char> span, char c)
        {
            return 0 < (uint)span.Length && span[0] == c;
        }

        public static bool EndsWith(this ReadOnlySpan<char> span, char c)
        {
            return 0 < (uint)span.Length && span[^1] == c;
        }
    }
}
