using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MihuBot.YoutubeArchive;

namespace MihuBot.API;

[ApiController]
[Route("api/YoutubeArchive")]
[Authorize(AuthenticationSchemes = YoutubeArchiveAuthenticationHandler.SchemeName)]
[RequestSizeLimit(256 * 1024)]
public sealed class YoutubeArchiveController(YoutubeArchiveService archive) : ControllerBase
{
    [HttpGet("Jobs")]
    public IActionResult Jobs() => Ok(archive.GetJobs(100));

    [HttpGet("Jobs/{id:guid}")]
    public IActionResult Job(Guid id)
    {
        YoutubeArchiveJob job = archive.GetJob(id);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpPost("Jobs")]
    public IActionResult Enqueue(YoutubeArchiveRequest request)
    {
        if (!YoutubeArchiveValidation.IsValid(request))
        {
            return BadRequest(new { error = "Supply a single YouTube video URL, mode audio/video, a supported maximum height (or null for best), and valid YouTube-only cookies. Audio must not specify a height." });
        }

        try
        {
            YoutubeArchiveJob job = archive.Enqueue(request);
            return AcceptedAtAction(nameof(Job), new { id = job.Id }, job);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("Metadata")]
    public Task<IActionResult> Metadata(string url, CancellationToken cancellationToken) =>
        GetMetadataAsync(new(url), cancellationToken);

    [HttpPost("Metadata")]
    public Task<IActionResult> MetadataWithCookies(YoutubeArchiveMetadataRequest request, CancellationToken cancellationToken) =>
        GetMetadataAsync(request, cancellationToken);

    private async Task<IActionResult> GetMetadataAsync(YoutubeArchiveMetadataRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !YoutubeHelper.TryParseVideoUrl(request.Url, out string videoId) || !YoutubeArchiveValidation.AreCookiesValid(request.Cookies))
        {
            return BadRequest(new { error = "A single YouTube video URL and valid YouTube-only cookies are required." });
        }

        try
        {
            return Ok(await archive.GetMetadataAsync(videoId, cancellationToken, request.Cookies));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "YouTube metadata lookup timed out. You can still select audio/video manually." });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }
}
