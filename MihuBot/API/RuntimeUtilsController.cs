using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
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

    [HttpGet("Jobs/Progress")]
    public async Task StreamProgress([FromQuery] string jobId)
    {
        if (!_jobs.TryGetJob(jobId, publicId: true, out var job))
        {
            Response.StatusCode = 404;
            return;
        }

        Response.Headers.ContentType = "text/event-stream; charset=utf-8";

        await job.StreamLogsAsync(new StreamWriter(Response.Body), HttpContext.RequestAborted);
    }

    [HttpPost("Jobs/Logs")]
    public IActionResult UploadLogs([FromQuery] string jobId, [FromBody] string[] lines)
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

        job.RawLogsReceived(lines);
        return Ok();
    }

    [HttpPost("Jobs/SystemInfo")]
    public IActionResult UpdateSystemInfo([FromQuery] string jobId, [FromBody] SystemHardwareInfo systemInfo, [FromQuery] string progressSummary)
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

    [HttpGet("Jobs/Metadata")]
    public IActionResult GetMetadata([FromQuery] string jobId)
    {
        if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
        {
            if (!_jobs.TryGetJob(jobId, publicId: true, out job) ||
                !ManagementController.CheckToken(Request.Headers, "X-Runtime-Utils-Token", _githubAuthToken))
            {
                return NotFound();
            }
        }

        if (job.InitialRemoteRunnerContact is null)
        {
            job.InitialRemoteRunnerContact = DateTime.UtcNow;
            job.Log("Initial remote runner contact");
        }

        return new JsonResult(job.Metadata);
    }

    [HttpGet("Jobs/Complete")]
    public IActionResult CompleteJob([FromQuery] string jobId)
    {
        if (!_jobs.TryGetJob(jobId, publicId: false, out var job))
        {
            return NotFound();
        }

        job.NotifyJobCompletion();
        return Ok();
    }

    [HttpPost("Jobs/Artifact")]
    [RequestSizeLimit(1536 * 1024 * 1024)] // 1.5 GB
    public async Task<IActionResult> UploadArtifact([FromQuery] string jobId, [FromQuery] string fileName)
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

    [HttpGet("Jobs/AnnounceRunner")]
    public async Task<IActionResult> AnnounceJobRunner([FromQuery] string jobType, [FromQuery] string runnerId)
    {
        if (string.IsNullOrWhiteSpace(jobType) ||
            string.IsNullOrWhiteSpace(runnerId) ||
            !_jobs.ConfigurationService.TryGet(null, $"RuntimeUtils.RunnerAnnounceToken.{runnerId}", out string expectedToken) ||
            !ManagementController.CheckToken(Request.Headers, "X-Runner-Announce-Token", expectedToken))
        {
            return NotFound();
        }

        if (HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } dataRateFeature)
        {
            dataRateFeature.MinDataRate = null;
        }

        if (HttpContext.Features.Get<IHttpRequestTimeoutFeature>() is { } timeoutFeature)
        {
            timeoutFeature.DisableTimeout();
        }

        return new JsonResult(await _jobs.AnnounceRunnerAsync(jobType, runnerId, HttpContext.RequestAborted));
    }
}
