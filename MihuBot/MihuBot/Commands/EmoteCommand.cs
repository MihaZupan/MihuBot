using Discord;
using MihuBot.Helpers;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace MihuBot.Commands
{
    public sealed class EmoteCommand : CommandBase
    {
        public override string Command => "emote";

        private readonly HttpClient _http;
        private readonly string _apiKey;

        public EmoteCommand(HttpClient httpClient, IConfiguration configuration)
        {
            _http = httpClient;
            _apiKey = configuration["Tenor:ApiKey"];
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync("emote") || ctx.Arguments.Length == 0 || ctx.Arguments.Length > 2)
                return;

            ulong guildId = Guilds.PrivateLogs;
            if (ctx.Arguments.Length == 2 && !ulong.TryParse(ctx.Arguments[1], out guildId))
            {
                await ctx.ReplyAsync("Unrecognized guild");
                return;
            }

            var guild = ctx.Discord.GetGuild(guildId);
            if (guild?.GetUser(ctx.BotId)?.GuildPermissions.ManageEmojisAndStickers != true)
            {
                await ctx.ReplyAsync("I don't have the permissions to do that");
                return;
            }

            const int Limit = 10;
            IMessage[] messages = (await ctx.Channel.GetMessagesAsync(Limit).ToArrayAsync())
                .SelectMany(i => i)
                .OrderByDescending(m => m.Timestamp)
                .ToArray();

            foreach (IMessage message in messages)
            {
                string url = null;
                string extension = null;

                if (message.Attachments.Count == 1)
                {
                    var attachment = message.Attachments.Single();
                    extension = Path.GetExtension(attachment.Filename).ToLowerInvariant();

                    if (extension == ".jpg" ||
                        extension == ".jpeg" ||
                        extension == ".png" ||
                        extension == ".gif")
                    {
                        url = attachment.Url;
                    }
                }

                if (url is null &&
                    message.Content.StartsWith("https://cdn.discordapp.com/", StringComparison.OrdinalIgnoreCase) &&
                    !message.Content.Contains(' ') &&
                    Uri.TryCreate(message.Content, UriKind.Absolute, out _))
                {
                    url = message.Content;
                    extension = Path.GetExtension(message.Content.SplitLastTrimmed('/'));
                }

                if (url is null &&
                    message.Content.StartsWith("https://tenor.com/view/", StringComparison.OrdinalIgnoreCase) &&
                    message.Content.Contains("-gif-", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(message.Content.SplitLastTrimmed('-'), out long id))
                {
                    try
                    {
                        string tenorJson = await _http.GetStringAsync($"https://api.tenor.com/v1/gifs?ids={id}&media_filter=minimal&key={_apiKey}");
                        url = JToken.Parse(tenorJson)["results"].First["media"].First["gif"]["url"].ToObject<string>();
                    }
                    catch (Exception ex)
                    {
                        ctx.DebugLog(ex);
                        break;
                    }

                    extension = ".gif";
                }

                if (url is null)
                    continue;

                extension = extension.SplitFirstTrimmed('?');
                extension = extension.ToLowerInvariant();
                string attachmentTempPath = Path.GetTempFileName() + extension;
                string convertedFileTempPath = Path.GetTempFileName() + extension;
                try
                {
                    using var stream = await _http.GetStreamAsync(url);

                    using (var fs = File.OpenWrite(attachmentTempPath))
                        await stream.CopyToAsync(fs);

                    using var proc = new Process();
                    proc.StartInfo.FileName = "ffmpeg";
                    proc.StartInfo.Arguments = $"-y -hide_banner -loglevel warning -i \"{attachmentTempPath}\" -vf scale=128:-1 \"{convertedFileTempPath}\"";
                    proc.StartInfo.UseShellExecute = false;
                    proc.Start();
                    proc.WaitForExit();

                    var emote = await guild.CreateEmoteAsync(ctx.Arguments[0], new Image(convertedFileTempPath));
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
