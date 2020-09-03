using Discord.WebSocket;
using MihuBot.Helpers;
using System;

namespace MihuBot
{
    public sealed class CommandContext : MessageContext
    {
        public readonly string Command;

        private string[] _arguments;
        public string[] Arguments
        {
            get
            {
                if (_arguments is null)
                {
                    var span = Content.AsSpan(Command.Length + 1);
                    int endOfLine = span.IndexOfAny('\n', '\r');
                    if (endOfLine != -1) span = span.Slice(0, endOfLine);

                    _arguments = span.Trim().ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }

                return _arguments;
            }
        }

        private string _argumentString;
        public string ArgumentString
        {
            get
            {
                if (_argumentString is null)
                {
                    _argumentString = Content.AsSpan(Command.Length + 1).Trim().ToString();
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
            int index = Content.AsSpan().IndexOfAny(' ', '\n', '\r');
            Command = Content.Substring(1, (index == -1 ? Content.Length : index) - 1).ToLowerInvariant();
        }
    }
}
