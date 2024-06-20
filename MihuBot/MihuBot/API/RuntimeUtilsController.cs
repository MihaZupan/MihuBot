using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MihuBot.Data;
using MihuBot.RuntimeUtils;

namespace MihuBot.API;

[Route("api/[controller]")]
[ApiController]
public sealed class RuntimeUtilsController : ControllerBase
{
    private readonly RuntimeUtilsService _jobs;
    private readonly string _githubAuthToken;

    private BadRequestResult JobCompletedErrorResult()
    {
        Response.Headers["X-Job-Completed"] = "true";
        return BadRequest();
    }

    public RuntimeUtilsController(RuntimeUtilsService jobs)
    {
        _jobs = jobs;
        _githubAuthToken = jobs.Configuration["RuntimeUtils:GitHubAuthToken"];
    }

    [HttpGet("Jobs/Progress/{jobId}")]
    public async Task StreamProgress([FromRoute] string jobId)
    {
        if (!_jobs.TryGetJob(jobId, publicId: true, out var job))
        {
            Response.StatusCode = 404;
            return;
        }

        Response.Headers.ContentType = "text/event-stream; charset=utf-8";

        await job.StreamLogsAsync(new StreamWriter(Response.Body), HttpContext.RequestAborted);
    }

    [HttpPost("Jobs/Logs/{jobId}")]
    public IActionResult UploadLogs([FromRoute] string jobId, [FromBody] string[] lines)
    {
        if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
        {
            return NotFound();
        }

        if (job.Completed)
        {
            return JobCompletedErrorResult();
        }

        if (lines is null)
        {
            return BadRequest();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] is null)
            {
                return BadRequest();
            }

            if (lines[i].Length > 10_000)
            {
                lines[i] = lines[i].TruncateWithDotDotDot(10_000);
            }
        }

        job.LogsReceived(lines);
        return Ok();
    }

    [HttpPost("Jobs/SystemInfo/{jobId}")]
    public IActionResult UpdateSystemInfo([FromRoute] string jobId, [FromBody] SystemHardwareInfo systemInfo, [FromQuery] string progressSummary)
    {
        if (systemInfo is null)
        {
            return BadRequest();
        }

        if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
        {
            return NotFound();
        }

        if (job.Completed)
        {
            return JobCompletedErrorResult();
        }

        if (string.IsNullOrWhiteSpace(progressSummary))
        {
            progressSummary = null;
        }

        job.LastSystemInfo = systemInfo;
        job.LastProgressSummary = progressSummary?.TruncateWithDotDotDot(100);
        return Ok();
    }

    [HttpGet("Jobs/Metadata/{jobId}")]
    public IActionResult GetMetadata([FromRoute] string jobId)
    {
        if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
        {
            if (!_jobs.TryGetJob(jobId, publicId: true, out job) ||
                !Request.Headers.TryGetValue("X-Runtime-Utils-Token", out var token) ||
                token.Count != 1 ||
                !ManagementController.CheckToken(_githubAuthToken, token.ToString()))
            {
                return NotFound();
            }
        }

        return new JsonResult(job.Metadata);
    }

    [HttpGet("Jobs/Complete/{jobId}")]
    public IActionResult CompleteJob([FromRoute] string jobId)
    {
        if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
        {
            return NotFound();
        }

        job.NotifyJobCompletion();
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

        if (job.Completed)
        {
            return JobCompletedErrorResult();
        }

        await job.ArtifactReceivedAsync(fileName, Request.Body, HttpContext.RequestAborted);
        return Ok();
    }
}
