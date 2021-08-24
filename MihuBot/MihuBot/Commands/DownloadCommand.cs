using Discord.WebSocket;
using MihuBot.Helpers;

namespace MihuBot.Commands
{
    public sealed class DownloadCommand : CommandBase
    {
        public override string Command => "download";
        public override string[] Aliases => new[] { "dl" };

        protected override int CooldownToleranceCount => 10;
        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(15);

        public override Task ExecuteAsync(CommandContext ctx)
        {
            SocketMessage msg = ctx.Channel
                .GetCachedMessages(10)
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault(m => m.Content.Contains("youtu", StringComparison.OrdinalIgnoreCase) && YoutubeHelper.TryParseVideoId(m.Content, out _));

            if (msg != null && YoutubeHelper.TryParseVideoId(msg.Content, out string videoId))
            {
                _ = Task.Run(async () => await YoutubeHelper.SendVideoAsync(videoId, ctx.Channel, useOpus: false));
            }

            return Task.CompletedTask;
        }
    }
}
