namespace MihuBot.Commands;

public sealed class UrlShortenerCommand : CommandBase
{
    public override string Command => "url";

    private readonly UrlShortenerService _urlShortener;

    public UrlShortenerCommand(UrlShortenerService urlShortener)
    {
        _urlShortener = urlShortener;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.Arguments.Length != 1 ||
            !Uri.TryCreate(ctx.Arguments[0], UriKind.Absolute, out Uri uri))
        {
            await ctx.ReplyAsync("Expected `!url https://www.youtube.com/watch?v=dQw4w9WgXcQ`");
            return;
        }

        var entry = await _urlShortener.CreateAsync($"{nameof(UrlShortenerCommand)}-{ctx.AuthorId}-{ctx.Message.GetJumpUrl()}", uri);

        await ctx.ReplyAsync($"<{entry.ShortUrl}>");
    }
}
