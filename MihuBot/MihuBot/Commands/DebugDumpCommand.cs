using Discord.WebSocket;
using MihuBot.Helpers;
using System.Collections.Generic;
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

            string dump;

            SocketGuild guild = ctx.Discord.GetGuild(id);
            if (guild is null)
            {
                SocketChannel channel = ctx.Discord.GetChannel(id);
                if (channel is null)
                {
                    await ctx.ReplyAsync("Unknown ID");
                    return;
                }

                dump = SerializeChannel(channel);
            }
            else
            {
                dump = SerializeGuild(guild);
            }

            await ctx.Channel.SendTextFileAsync($"{id}.txt", dump);
        }

        private static string SerializeChannel(SocketChannel channel)
        {
            var sb = new StringBuilder();

            IReadOnlyCollection<SocketGuildUser> users;

            if (channel is SocketTextChannel textChannel)
            {
                sb.Append(textChannel.Name);
                users = textChannel.Users;
            }
            else if (channel is SocketVoiceChannel voiceChannel)
            {
                sb.Append(voiceChannel.Name);
                users = voiceChannel.Users;
            }
            else
            {
                return channel.GetType().FullName;
            }

            sb.Append(" (").Append(channel.Id).AppendLine(")");
            sb.AppendLine();

            foreach (SocketUser user in users)
            {
                sb.Append(user.Id.ToString().PadRight(21, ' ')).AppendLine(user.Username);
            }

            return sb.ToString();
        }

        private static string SerializeGuild(SocketGuild guild)
        {
            var sb = new StringBuilder();

            sb.Append(guild.Name).Append(" (").Append(guild.Id).AppendLine(")");
            sb.Append(guild.MemberCount).Append(" members");
            sb.AppendLine(guild.Description);
            sb.AppendLine();

            foreach (SocketTextChannel channel in guild.TextChannels)
            {
                sb.Append("Text channel ").Append(channel.Name).Append(" (").Append(channel.Id).AppendLine(" )");
            }
            sb.AppendLine();

            foreach (SocketVoiceChannel channel in guild.VoiceChannels)
            {
                sb.Append("Voice channel ").Append(channel.Name).Append(" (").Append(channel.Id).AppendLine(" )");
            }

            return sb.ToString();
        }
    }
}
