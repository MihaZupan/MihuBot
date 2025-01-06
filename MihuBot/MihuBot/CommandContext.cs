using MihuBot.Permissions;

namespace MihuBot;

public sealed class CommandContext : MessageContext
{
    public readonly string Command;

    private readonly IPermissionsService _permissions;

    public string[] Arguments
    {
        get
        {
            if (field is null)
            {
                ReadOnlySpan<char> span = Content.AsSpan(Command.Length + 1);
                int endOfLine = span.IndexOfAny('\r', '\n');
                if (endOfLine != -1) span = span.Slice(0, endOfLine);

                field = span.Trim().ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }

            return field;
        }
    }

    public string ArgumentString =>
        field ??= Content.Length <= Command.Length
            ? string.Empty
            : Content.AsSpan(Command.Length + 1).Trim().ToString();

    public string[] ArgumentLines
    {
        get
        {
            if (field is null)
            {
                string[] lines = ArgumentString.SplitLines();

                for (int i = 0; i < lines.Length; i++)
                    lines[i] = lines[i].Trim();

                field = lines;
            }

            return field;
        }
    }

    public string ArgumentStringTrimmed => field ??= string.Join('\n', ArgumentLines);

    public CommandContext(DiscordSocketClient discord, SocketUserMessage message, string command, Logger logger, IPermissionsService permissions, CancellationToken cancellationToken)
        : base(discord, message, logger, cancellationToken)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
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
