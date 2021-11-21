namespace MihuBot.Commands
{
    public sealed class DebugDumpCommand : CommandBase
    {
        public override string Command => "debugdump";
        public override string[] Aliases => new[] { "pdebugdump" };

        private readonly DiscordSocketClient _privateClient;

        public DebugDumpCommand(IEnumerable<CustomLogger> customLogger)
        {
            _privateClient = customLogger.FirstOrDefault()?.Options.Discord;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync(Command))
            {
                return;
            }

            if (ctx.Arguments.Length < 1 || !ulong.TryParse(ctx.Arguments[0], out ulong id))
            {
                return;
            }

            ulong? secondId = null;
            if (ctx.Arguments.Length >= 2 && ulong.TryParse(ctx.Arguments[1], out ulong secondIdTemp))
            {
                secondId = secondIdTemp;
            }

            DiscordSocketClient discord = ctx.Command == "pdebugdump" ? _privateClient : null;
            discord ??= ctx.Discord;

            var sb = new StringBuilder();

            SocketGuild guild = discord.GetGuild(id);
            if (guild is null)
            {
                SocketChannel channel = discord.GetChannel(id);
                if (channel is null)
                {
                    await ctx.ReplyAsync("Unknown ID");
                    return;
                }

                SerializeChannel(channel, sb);
            }
            else if (secondId.HasValue && guild.GetRole(secondId.Value) is SocketRole role)
            {
                SerializeRole(role, sb);
            }
            else if (secondId.HasValue && guild.GetUser(secondId.Value) is SocketGuildUser user)
            {
                SerializeUser(user, sb);
            }
            else
            {
                SerializeGuild(guild, sb);
            }

            await ctx.Channel.SendTextFileAsync($"{id}.txt", sb.ToString());
        }

        private static void SerializeRole(SocketRole role, StringBuilder sb)
        {
            sb.Append(role.Id).AppendLine();
            sb.AppendLine(role.Name);
            sb.AppendLine();

            foreach (SocketUser member in role.Members)
            {
                sb.Append(member.Id.ToString().PadRight(21, ' ')).AppendLine(member.Username);
            }

            sb.AppendLine();

            SerializePermissions(role.Permissions, sb);
        }

        private static void SerializeChannel(SocketChannel channel, StringBuilder sb)
        {
            if (channel is SocketTextChannel textChannel)
            {
                sb.Append(channel.Id).AppendLine();
                sb.AppendLine(textChannel.Name);
                sb.AppendLine();

                foreach (SocketUser user in textChannel.Users)
                {
                    sb.Append(user.Id.ToString().PadRight(21, ' ')).AppendLine(user.Username);
                }
            }
            else if (channel is SocketVoiceChannel voiceChannel)
            {
                sb.Append(channel.Id).AppendLine();
                sb.AppendLine(voiceChannel.Name);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(channel.GetType().FullName);
                return;
            }

            if (channel is not SocketGuildChannel guildChannel)
            {
                return;
            }

            SocketGuild guild = guildChannel.Guild;

            sb.AppendLine();

            foreach (Overwrite overwrite in guildChannel.PermissionOverwrites)
            {
                if (overwrite.TargetType == PermissionTarget.Role)
                {
                    sb.Append("Role ").Append(overwrite.TargetId);
                    if (guild.GetRole(overwrite.TargetId) is SocketRole role)
                    {
                        sb.Append(" (").Append(role.Name).Append(')');
                    }
                }
                else if (overwrite.TargetType == PermissionTarget.User)
                {
                    sb.Append("User ").Append(overwrite.TargetId);
                    if (guild.GetUser(overwrite.TargetId) is SocketUser user)
                    {
                        sb.Append(" (").Append(user.Username).Append(')');
                    }
                }
                sb.AppendLine();

                SerializePermissions(overwrite.Permissions, sb);

                sb.AppendLine();
            }
        }

        private static void SerializeGuild(SocketGuild guild, StringBuilder sb)
        {
            sb.Append(guild.Name).Append(" (").Append(guild.Id).AppendLine(")");
            sb.Append(guild.MemberCount).AppendLine(" members");
            sb.AppendLine(guild.Description);
            sb.AppendLine();

            sb.AppendLine("Text channels:");
            foreach (SocketTextChannel channel in guild.TextChannels.OrderBy(c => c.Position))
            {
                sb.Append(channel.Id.ToString().PadRight(19, ' ')).AppendLine(channel.Name);
            }
            sb.AppendLine();

            sb.AppendLine("Voice channels:");
            foreach (SocketVoiceChannel channel in guild.VoiceChannels.OrderBy(c => c.Position))
            {
                sb.Append(channel.Id.ToString().PadRight(19, ' ')).AppendLine(channel.Name);
            }
        }

        private static void SerializeUser(SocketGuildUser user, StringBuilder sb)
        {
            sb.Append(user.Username).Append('#').Append(user.Discriminator).Append(" (").Append(user.Id).AppendLine(")");

            if (user.JoinedAt.HasValue)
            {
                sb.Append("Joined at ").AppendLine(user.JoinedAt.Value.ToISODate());
            }

            sb.AppendLine();

            SerializePermissions(user.GuildPermissions, sb);
            sb.AppendLine();

            foreach (SocketTextChannel channel in user.Guild.Channels.OrderBy(c => c.Position))
            {
                OverwritePermissions? permOverwrites = channel.GetPermissionOverwrite(user);
                if (permOverwrites.HasValue && (permOverwrites.Value.AllowValue != 0 || permOverwrites.Value.DenyValue != 0))
                {
                    sb.Append(channel.Id.ToString().PadRight(19, ' ')).AppendLine(channel.Name);
                    SerializePermissions(permOverwrites.Value, sb);
                    sb.AppendLine();
                }
            }
        }

        private static void SerializePermissions(GuildPermissions permissions, StringBuilder sb)
        {
            sb.Append("Allow: ");
            foreach (ChannelPermission perm in permissions.ToList())
            {
                sb.Append(perm).Append(' ');
            }
            sb.AppendLine();
        }

        private static void SerializePermissions(OverwritePermissions overwrite, StringBuilder sb)
        {
            sb.Append("Allow: ");
            foreach (ChannelPermission allow in overwrite.ToAllowList())
            {
                sb.Append(allow).Append(' ');
            }
            sb.AppendLine();

            sb.Append("Deny: ");
            foreach (ChannelPermission deny in overwrite.ToDenyList())
            {
                sb.Append(deny).Append(' ');
            }
            sb.AppendLine();
        }
    }
}
