using Microsoft.AspNetCore.Mvc;

namespace MihuBot.API;

[ApiController]
public sealed class UrlShortenerController : ControllerBase
{
    private readonly UrlShortenerService _urlShortener;

    public UrlShortenerController(UrlShortenerService urlShortener)
    {
        _urlShortener = urlShortener;
    }

    [HttpGet("r/{id}")]
    public async Task<IActionResult> Get([FromRoute] string id)
    {
        var entry = await _urlShortener.GetAsync(id);

        if (entry is null)
        {
            return NotFound();
        }

        return Redirect(entry.OriginalUrl);
    }
}
