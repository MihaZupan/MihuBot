using Discord.WebSocket;
using MihuBot.Helpers;
using System;

namespace MihuBot
{
    public sealed class CommandContext : MessageContext
    {
        public readonly string Command;
        public readonly string[] Parts;
        public readonly string[] Arguments;

        private string _argumentString;
        public string ArgumentString
        {
            get
            {
                if (_argumentString is null)
                {
                    _argumentString = Content.AsSpan(Parts[0].Length).Trim().ToString();
                }

                return _argumentString;
            }
        }

        private string[] _argumentLines;
        public string[] ArgumentLines
        {
            get
            {
                if (_argumentLines is null)
                {
                    string[] lines = ArgumentString.SplitLines();

                    for (int i = 0; i < lines.Length; i++)
                        lines[i] = lines[i].Trim();

                    _argumentLines = lines;
                }

                return _argumentLines;
            }
        }

        private string _argumentStringTrimmed;
        public string ArgumentStringTrimmed
        {
            get
            {
                if (_argumentStringTrimmed is null)
                {
                    _argumentStringTrimmed = string.Join('\n', ArgumentLines);
                }

                return _argumentStringTrimmed;
            }
        }

        public CommandContext(ServiceCollection services, SocketMessage message)
            : base(services, message)
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
        }
    }
}
