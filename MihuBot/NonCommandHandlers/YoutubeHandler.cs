using Google.Apis.YouTube.v3;

namespace MihuBot.NonCommandHandlers;

public sealed class YoutubeHandler : NonCommandHandler
{
    private readonly YouTubeService _youtubeService;

    public YoutubeHandler(YouTubeService youtubeService)
    {
        _youtubeService = youtubeService ?? throw new ArgumentNullException(nameof(youtubeService));
    }

    public override Task HandleAsync(MessageContext ctx)
    {
        if (ctx.Content.Contains("youtu", StringComparison.OrdinalIgnoreCase) && ctx.IsMentioned)
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
                _ = Task.Run(async () => await YoutubeHelper.SendPlaylistAsync(playlistId, ctx.Channel, useOpus, _youtubeService));
            }
        }

        return Task.CompletedTask;
    }
}
