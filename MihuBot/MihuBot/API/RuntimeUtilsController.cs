using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SpotifyAPI.Web;

namespace MihuBot.API
{
    [Route("api/[controller]")]
    [ApiController]
    public sealed class RuntimeUtilsController : ControllerBase
    {
        private readonly RuntimeUtilsService _jobs;

        public RuntimeUtilsController(RuntimeUtilsService jobs)
        {
            _jobs = jobs;
        }

        [HttpGet("Jobs/Progress/{jobId}")]
        public async Task StreamProgress([FromRoute] string jobId)
        {
            if (!_jobs.TryGetJob(jobId, publicId: true, out var job))
            {
                Response.StatusCode = 404;
                return;
            }

            Response.Headers.Add("Content-Type", "text/event-stream");

            await job.StreamLogsAsync(new StreamWriter(Response.Body), HttpContext.RequestAborted);
        }

        [HttpPost("Jobs/Logs/{jobId}")]
        public IActionResult UploadLogs([FromRoute] string jobId, [FromBody] string[] lines)
        {
            if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
            {
                return NotFound();
            }

            job.LogsReceived(lines);
            return Ok();
        }

        [HttpPost("Jobs/Artifact/{jobId}/{fileName}")]
        [RequestSizeLimit(1536 * 1024 * 1024)] // 1.5 GB
        public async Task<IActionResult> UploadArtifact([FromRoute] string jobId, [FromRoute] string fileName)
        {
            if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
            {
                return NotFound();
            }

            await job.ArtifactReceivedAsync(fileName, Request.Body, HttpContext.RequestAborted);
            return Ok();
        }
    }
}
