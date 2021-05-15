using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class ColorRoles : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(5);

        protected override int CooldownToleranceCount => 3;

        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, GuildColors>> _guildColors = new("ColorRoles.json");

        private sealed class GuildColors
        {
            public readonly Dictionary<ulong, uint> Users = new(); // UserId => Color
            public readonly Dictionary<uint, ulong> Roles = new(); // Color  => RoleId
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            string content = ctx.Content;

            if (content.StartsWith('#') &&
                content.Length == 7 &&
                CharHelper.TryParseHex(content[1], content[2], out int r) &&
                CharHelper.TryParseHex(content[3], content[4], out int g) &&
                CharHelper.TryParseHex(content[5], content[6], out int b) &&
                ctx.GuildPermissions.ManageRoles)
            {
                return HandleAsyncCore(new Color(r, g, b));
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore(Color newColor)
            {
                if (!await TryEnterOrWarnAsync(ctx))
                    return;

                var colors = await _guildColors.EnterAsync();
                try
                {
                    if (!colors.TryGetValue(ctx.Guild.Id, out var guildColors))
                        guildColors = colors[ctx.Guild.Id] = new GuildColors();

                    SocketRole previousRole = null;

                    if (guildColors.Users.TryGetValue(ctx.AuthorId, out uint previousColor))
                    {
                        if (previousColor == newColor.RawValue)
                            return;

                        if (guildColors.Roles.TryGetValue(previousColor, out ulong previousRoleId))
                        {
                            previousRole = ctx.Guild.GetRole(previousRoleId);
                        }
                    }

                    guildColors.Users[ctx.AuthorId] = newColor.RawValue;

                    if (!guildColors.Roles.TryGetValue(newColor.RawValue, out ulong newRoleId))
                    {
                        string name = ctx.Content.ToUpperInvariant();
                        var createdRole = await ctx.Guild.CreateRoleAsync(name, color: newColor, isMentionable: false);
                        newRoleId = createdRole.Id;
                        guildColors.Roles.Add(newColor.RawValue, newRoleId);
                    }

                    var role = ctx.Guild.GetRole(newRoleId);

                    await ctx.Author.AddRoleAsync(role);

                    if (previousRole is not null)
                    {
                        if (guildColors.Users.ContainsValue(previousColor))
                        {
                            await ctx.Author.RemoveRoleAsync(previousRole);
                        }
                        else
                        {
                            await previousRole.DeleteAsync();
                            guildColors.Roles.Remove(previousColor);
                        }
                    }
                }
                finally
                {
                    _guildColors.Exit();
                }
            }
        }
    }
}
