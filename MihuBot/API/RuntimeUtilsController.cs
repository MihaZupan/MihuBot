using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;
using MihuBot.Helpers.Crypto;
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
    public async Task StreamProgress([FromQuery] string jobId, [FromQuery] bool live = false, [FromQuery] int? tail = null)
    {
        if (tail is < 0 or > 100_000)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (_jobs.TryGetJob(jobId, publicId: true, out var job))
        {
            Response.Headers.ContentType = live
                ? "text/event-stream; charset=utf-8"
                : "text/plain; charset=utf-8";

            using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true);

            if (live)
            {
                await job.StreamLogsAsync(writer, tail, HttpContext.RequestAborted);
            }
            else
            {
                foreach (string line in job.GetLogSnapshot(tail))
                {
                    await writer.WriteLineAsync(line.AsMemory(), HttpContext.RequestAborted);
                }
            }

            return;
        }

        CompletedJobRecord completed = await _jobs.TryGetCompletedJobRecordAsync(jobId, HttpContext.RequestAborted);
        if (completed?.LogsArtifactUrl is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.Headers.ContentType = "text/plain; charset=utf-8";
        using var completedWriter = new StreamWriter(Response.Body, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true);
        foreach (string line in await _jobs.GetCompletedJobLogsAsync(completed, tail, HttpContext.RequestAborted))
        {
            await completedWriter.WriteLineAsync(line.AsMemory(), HttpContext.RequestAborted);
        }
    }

    [HttpPost("Jobs")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<ActionResult<PatchJobSubmissionResponse>> SubmitJob([FromBody] PatchJobRequest request, CancellationToken cancellationToken)
    {
        string githubToken = null;
        if (Request.Headers.TryGetValue(HeaderNames.Authorization, out var authorization) &&
            authorization.Count == 1)
        {
            const string BearerPrefix = "Bearer ";
            string value = authorization[0];
            if (!value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Unauthorized();
            }

            githubToken = value.Substring(BearerPrefix.Length).Trim();
            if (githubToken.Length == 0)
            {
                return Unauthorized();
            }
        }

        try
        {
            return Ok(await _jobs.StartPatchJobAsync(request, githubToken, cancellationToken));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid job request", Detail = ex.Message });
        }
        catch (RuntimeUtilsCapacityException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Runtime-utils capacity exhausted",
                Detail = ex.Message,
            });
        }
    }

    [HttpGet("Jobs/Status")]
    public async Task<ActionResult<RuntimeUtilsJobStatusResponse>> GetJobStatus([FromQuery] string jobId, CancellationToken cancellationToken)
    {
        RuntimeUtilsJobStatusResponse status = await _jobs.TryGetJobStatusAsync(jobId, cancellationToken);
        return status is null ? NotFound() : status;
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
                !TokenHelper.CheckToken(Request.Headers, "X-Runtime-Utils-Token", _githubAuthToken))
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

    [HttpGet("Jobs/Patch")]
    public IActionResult GetPatch([FromQuery] string jobId)
    {
        if (!_jobs.TryGetJob(jobId, publicId: false, out JobBase job) ||
            job.PatchContent is null)
        {
            return NotFound();
        }

        return Content(job.PatchContent, "text/x-diff", Encoding.UTF8);
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
    public async Task<IActionResult> AnnounceJobRunner(
        [FromQuery] string jobType,
        [FromQuery] string runnerId,
        [FromQuery] string os,
        [FromQuery] string architecture,
        [FromQuery] string baseRepo,
        [FromQuery] string baseBranch)
    {
        if (string.IsNullOrWhiteSpace(jobType) ||
            string.IsNullOrWhiteSpace(runnerId) ||
            string.IsNullOrWhiteSpace(os) ||
            string.IsNullOrWhiteSpace(architecture) ||
            string.IsNullOrWhiteSpace(baseRepo) ||
            string.IsNullOrWhiteSpace(baseBranch) ||
            !_jobs.ConfigurationService.TryGet(null, $"RuntimeUtils.RunnerAnnounceToken.{runnerId}", out string expectedToken) ||
            !TokenHelper.CheckToken(Request.Headers, "X-Runner-Announce-Token", expectedToken))
        {
            return NotFound();
        }

        var capabilities = new RunnerCapabilities(jobType, os, architecture, baseRepo, baseBranch);

        if (HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } dataRateFeature)
        {
            dataRateFeature.MinDataRate = null;
        }

        if (HttpContext.Features.Get<IHttpRequestTimeoutFeature>() is { } timeoutFeature)
        {
            timeoutFeature.DisableTimeout();
        }

        return new JsonResult(await _jobs.AnnounceRunnerAsync(runnerId, capabilities, HttpContext.RequestAborted));
    }
}
