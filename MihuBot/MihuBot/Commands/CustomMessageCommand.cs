using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class CustomMessageCommand : CommandBase
    {
        public override string Command => "message";
        public override string[] Aliases => new[] { "msg" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            string[] lines = ctx.Content.Split('\n');
            string[] headers = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (headers.Length < 3)
            {
                await ctx.ReplyAsync("Missing command arguments, try ```\n!message guildId channelId\nMessage text\n````");
                return;
            }

            if (!ulong.TryParse(headers[1], out ulong guildId) || !Constants.GuildIDs.Contains(guildId))
            {
                string guilds = string.Join('\n', Constants.GuildIDs.Select(id => id + ": " + ctx.Client.GetGuild(id).Name));
                await ctx.ReplyAsync("Invalid Guild ID. Try:\n```\n" + guilds + "\n```");
                return;
            }

            if (!ulong.TryParse(headers[2], out ulong channelId))
            {
                await ctx.ReplyAsync("Invalid channel ID format");
                return;
            }

            SocketGuild guild = ctx.Client.GetGuild(guildId);

            if (!(guild.TextChannels.FirstOrDefault(c => c.Id == channelId) is SocketTextChannel channel))
            {
                string channels = string.Join('\n', guild.Channels.Select(c => c.Id + ":\t" + c.Name));
                if (channels.Length > 500)
                {
                    channels = string.Concat(channels.AsSpan(0, channels.AsSpan(0, 496).LastIndexOf('\n')), " ...");
                }

                await ctx.ReplyAsync("Unknown channel. Try:\n```\n" + channels + "\n```");
                return;
            }

            try
            {
                if (lines[1].AsSpan().TrimStart().StartsWith('{') && lines[^1].AsSpan().TrimEnd().EndsWith('}'))
                {
                    int first = ctx.Content.IndexOf('{');
                    int last = ctx.Content.LastIndexOf('}');
                    await EmbedHelper.SendEmbedAsync(ctx.Content.Substring(first, last - first + 1), channel);
                }
                else
                {
                    await channel.SendMessageAsync(ctx.Content.AsSpan(lines[0].Length + 1).Trim(stackalloc char[] { ' ', '\t', '\r', '\n' }).ToString());
                }
            }
            catch (Exception ex)
            {
                await ctx.ReplyAsync(ex.Message, mention: true);
                throw;
            }
        }
    }
}
