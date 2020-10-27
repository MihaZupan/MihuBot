using Discord.WebSocket;
using MihuBot.Helpers;
using MihuBot.Permissions;
using System;
using System.Threading.Tasks;

namespace MihuBot
{
    public sealed class CommandContext : MessageContext
    {
        public readonly string Command;

        private readonly IPermissionsService _permissions;

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

        public CommandContext(DiscordSocketClient discord, SocketUserMessage message, string command, Logger logger, IPermissionsService permissions)
            : base(discord, message, logger)
        {
            Command = command;
            _permissions = permissions;
        }

        public ValueTask<bool> RequirePermissionAsync(string permission)
        {
            if (HasPermission(permission))
            {
                return new ValueTask<bool>(true);
            }
            else
            {
                return WarnAsync(this, permission);

                static async ValueTask<bool> WarnAsync(CommandContext ctx, string permission)
                {
                    ctx.DebugLog($"Missing permission {permission}");
                    await ctx.ReplyAsync($"Missing permission `{permission}`", mention: true);
                    return false;
                }
            }
        }

        public bool HasPermission(string permission) =>
            _permissions.HasPermission(permission, AuthorId);
    }
}
