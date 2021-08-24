using MihuBot.Permissions;

namespace MihuBot.Commands
{
    public sealed class PermissionsCommand : CommandBase
    {
        public override string Command => "permissions";
        public override string[] Aliases => new[] { "permission", "perm", "perms" };

        private readonly IPermissionsService _permissions;

        public PermissionsCommand(IPermissionsService permissions)
        {
            _permissions = permissions;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            const string Usage = "Usage: `!permissions [check/add/remove] permission userId`";

            if (ctx.Arguments.Length != 3 || !ulong.TryParse(ctx.Arguments[2], out ulong userId))
            {
                await ctx.ReplyAsync(Usage);
                return;
            }

            string permission = ctx.Arguments[1];

            switch (ctx.Arguments[0].ToLowerInvariant())
            {
                case "check":
                    if (await ctx.RequirePermissionAsync("permissions.read"))
                    {
                        bool hasPermission = _permissions.HasPermission(permission, userId);
                        await ctx.ReplyAsync($"`{permission}: {hasPermission}`");
                    }
                    break;

                case "add":
                    if (await ctx.RequirePermissionAsync("permissions.write"))
                    {
                        await _permissions.AddPermissionAsync(permission, userId);
                        await ctx.ReplyAsync($"Added `{permission}` for {userId}");
                    }
                    break;

                case "remove":
                    if (await ctx.RequirePermissionAsync("permissions.write"))
                    {
                        await _permissions.RemovePermissionAsync(permission, userId);
                        await ctx.ReplyAsync($"Removed `{permission}` for {userId}");
                    }
                    break;

                default:
                    await ctx.ReplyAsync(Usage);
                    break;
            }
        }
    }
}
