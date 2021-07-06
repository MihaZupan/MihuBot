using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using System.Text;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class DebugDumpCommand : CommandBase
    {
        public override string Command => "debugdump";

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

            var sb = new StringBuilder();

            SocketGuild guild = ctx.Discord.GetGuild(id);
            if (guild is null)
            {
                SocketChannel channel = ctx.Discord.GetChannel(id);
                if (channel is null)
                {
                    await ctx.ReplyAsync("Unknown ID");
                    return;
                }

                SerializeChannel(channel, sb);
            }
            else if (ctx.Arguments.Length >= 2 && ulong.TryParse(ctx.Arguments[1], out ulong secondId)
                && guild.GetRole(secondId) is SocketRole role)
            {
                SerializeRole(role, sb);
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

            sb.AppendLine("Allow:");
            foreach (ChannelPermission allow in role.Permissions.ToList())
            {
                sb.Append(allow).Append(' ');
            }
            sb.AppendLine();
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

                sb.Append("Allow: ");
                foreach (ChannelPermission allow in overwrite.Permissions.ToAllowList())
                {
                    sb.Append(allow).Append(' ');
                }
                sb.AppendLine();

                sb.Append("Deny: ");
                foreach (ChannelPermission deny in overwrite.Permissions.ToDenyList())
                {
                    sb.Append(deny).Append(' ');
                }
                sb.AppendLine();

                sb.AppendLine();
            }
        }

        private static void SerializeGuild(SocketGuild guild, StringBuilder sb)
        {
            sb.Append(guild.Name).Append(" (").Append(guild.Id).AppendLine(")");
            sb.Append(guild.MemberCount).Append(" members");
            sb.AppendLine(guild.Description);
            sb.AppendLine();

            foreach (SocketTextChannel channel in guild.TextChannels)
            {
                sb.Append("Text channel ").Append(channel.Id.ToString().PadRight(21, ' ')).AppendLine(channel.Name);
            }
            sb.AppendLine();

            foreach (SocketVoiceChannel channel in guild.VoiceChannels)
            {
                sb.Append("Voice channel ").Append(channel.Id.ToString().PadRight(21, ' ')).AppendLine(channel.Name);
            }
        }
    }
}
