using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MihuBot.RuntimeUtils;

namespace MihuBot.API;

[Route("api/RuntimeUtils/[controller]")]
[ApiController]
public sealed partial class CoreRootController : ControllerBase
{
    private readonly RuntimeUtilsService _runtimeUtils;
    private readonly CoreRootService _coreRoot;

    public CoreRootController(RuntimeUtilsService runtimeUtils, CoreRootService coreRoot)
    {
        _runtimeUtils = runtimeUtils;
        _coreRoot = coreRoot;
    }

    [HttpGet("List")]
    public async Task<IEnumerable<CoreRootService.CoreRootEntry>> List(string range, string arch, string os, string type = "release")
    {
        if (!CoreRootService.TryValidate(ref arch, ref os, ref type) ||
            string.IsNullOrEmpty(range) || GitRangeRegex().Match(range) is not { Success: true } gitRangeMatch)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return [];
        }

        return await _coreRoot.ListAsync(gitRangeMatch.Groups[1].Value, gitRangeMatch.Groups[2].Value, arch, os, type);
    }

    [HttpGet("All")]
    public async Task<IEnumerable<CoreRootService.CoreRootEntry>> All(string arch, string os, string type = "release")
    {
        if (!CoreRootService.TryValidate(ref arch, ref os, ref type))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return [];
        }

        return await _coreRoot.AllAsync(arch, os, type);
    }

    [HttpGet("Get")]
    public async Task<CoreRootService.CoreRootEntry> Get(string sha, string arch, string os, string type = "release")
    {
        if (!CoreRootService.TryValidate(ref arch, ref os, ref type) ||
            string.IsNullOrEmpty(sha) || !ShaRegex().IsMatch(sha))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }

        return await _coreRoot.GetAsync(sha, arch, os, type);
    }

    [HttpGet("Save")]
    public async Task<IActionResult> Save(string jobId, string sha, string arch, string os, string type, string blobName)
    {
        if (!CoreRootService.TryValidate(ref arch, ref os, ref type) ||
            string.IsNullOrEmpty(sha) || !ShaRegex().IsMatch(sha) ||
            !_runtimeUtils.TryGetJob(jobId, publicId: false, out JobBase job) || job is not CoreRootGenerationJob)
        {
            return BadRequest();
        }

        if (!await _coreRoot.SaveAsync(sha, arch, os, type, blobName))
        {
            return BadRequest();
        }

        return Ok();
    }

    [GeneratedRegex(@"^[a-f0-9]{40}$")]
    private static partial Regex ShaRegex();

    [GeneratedRegex(@"^([a-f0-9]{40})\.\.\.([a-f0-9]{40})$")]
    private static partial Regex GitRangeRegex();
}
