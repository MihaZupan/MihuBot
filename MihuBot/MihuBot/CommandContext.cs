using Discord.WebSocket;
using System;

namespace MihuBot
{
    public sealed class CommandContext : MessageContext
    {
        public readonly string Command;
        public readonly string[] Parts;
        public readonly string[] Arguments;
        public readonly string ArgumentString;

        public CommandContext(DiscordSocketClient client, SocketMessage message)
            : base(client, message)
        {
            Parts = Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Command = Parts[0].Substring(1).ToLowerInvariant();

            if (Parts.Length > 1)
            {
                Arguments = new string[Parts.Length - 1];
                Array.Copy(Parts, 1, Arguments, 0, Arguments.Length);
            }
            else
            {
                Arguments = Array.Empty<string>();
            }

            ArgumentString = Content.AsSpan(Parts[0].Length).Trim().ToString();
        }
    }
}
