using Discord;
using MihuBot.Helpers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class EmoteCommand : CommandBase
    {
        public override string Command => "emote";

        private readonly HttpClient _http;

        public EmoteCommand(HttpClient httpClient)
        {
            _http = httpClient;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin || ctx.Arguments.Length == 0 || ctx.Arguments.Length > 2)
                return;

            ulong guildId = Guilds.PrivateLogs;
            if (ctx.Arguments.Length == 2 && !(ulong.TryParse(ctx.Arguments[1], out guildId) && Constants.GuildIDs.Contains(guildId)))
            {
                await ctx.ReplyAsync("Unrecognized guild");
                return;
            }

            const int Limit = 10;
            IMessage[] messages = (await ctx.Channel.GetMessagesAsync(Limit).ToArrayAsync())
                .SelectMany(i => i)
                .Where(m => m.Attachments.Any())
                .OrderByDescending(m => m.Timestamp)
                .ToArray();

            foreach (IMessage message in messages)
            {
                var attachment = message.Attachments.Single();
                string extension = Path.GetExtension(attachment.Filename).ToLowerInvariant();

                if (extension == ".jpg" ||
                    extension == ".jpeg" ||
                    extension == ".png" ||
                    extension == ".gif")
                {
                    string attachmentTempPath = Path.GetTempFileName() + extension;
                    string convertedFileTempPath = Path.GetTempFileName() + extension;
                    try
                    {
                        using var stream = await _http.GetStreamAsync(attachment.Url);

                        using (var fs = File.OpenWrite(attachmentTempPath))
                            await stream.CopyToAsync(fs);

                        using var proc = new Process();
                        proc.StartInfo.FileName = "ffmpeg";
                        proc.StartInfo.Arguments = $"-y -hide_banner -loglevel warning -i \"{attachmentTempPath}\" -vf scale=128:-1 \"{convertedFileTempPath}\"";
                        proc.StartInfo.UseShellExecute = false;
                        proc.Start();
                        proc.WaitForExit();

                        var emote = await ctx.Discord.GetGuild(guildId).CreateEmoteAsync(ctx.Arguments[0], new Image(convertedFileTempPath));
                        await ctx.ReplyAsync($"Created emote {emote.Name}: {emote}");

                        break;
                    }
                    finally
                    {
                        try { File.Delete(attachmentTempPath); } catch { }
                        try { File.Delete(convertedFileTempPath); } catch { }
                    }
                }
            }
        }
    }
}
