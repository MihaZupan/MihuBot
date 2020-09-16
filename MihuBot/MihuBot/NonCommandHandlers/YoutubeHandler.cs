using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class YoutubeHandler : NonCommandHandler
    {
        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.IsMentioned && ctx.Content.Contains("youtu", StringComparison.OrdinalIgnoreCase))
            {
                bool useOpus = ctx.Content.EndsWith(" opus", StringComparison.OrdinalIgnoreCase);

                string videoId = null, playlistId = null;
                var parts = ctx.Content.Split(' ').Where(p => p.Contains("youtu", StringComparison.OrdinalIgnoreCase));
                if (parts.Any(p => YoutubeHelper.TryParseVideoId(p, out videoId)))
                {
                    _ = Task.Run(async () => await YoutubeHelper.SendVideoAsync(videoId, ctx.Channel, useOpus));
                }
                else if (parts.Any(p => YoutubeHelper.TryParsePlaylistId(p, out playlistId)))
                {
                    _ = Task.Run(async () => await YoutubeHelper.SendPlaylistAsync(playlistId, ctx.Channel, useOpus));
                }
            }

            return Task.CompletedTask;
        }
    }
}
