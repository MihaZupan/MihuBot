using System.ComponentModel;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.Server;

namespace MihuBot.RuntimeUtils.AI;

[McpServerToolType]
public sealed class RuntimeUtilsMcpServer(
    RuntimeUtilsService RuntimeUtils,
    IHttpContextAccessor HttpContextAccessor)
{
    [McpServerTool(Name = "start_runtime_utils_patch_job", Title = "Start a runtime-utils job from a patch")]
    [Description(
        "Starts a JitDiff, BenchmarkLibraries, or RegexDiff job by applying a git-compatible unified diff to a baseline branch. " +
        "See https://gist.github.com/MihaZupan/6bcaacbe025265aa457ea7bb9a4dbcae for supported arguments and usage. " +
        "Without GitHub authentication the job uses a public Helix runner. An authorized GitHub caller may use Azure by " +
        "sending a GitHub access token with the bearer authentication scheme on the MCP HTTP request.")]
    public Task<PatchJobSubmissionResponse> StartPatchJobAsync(
        [Description("Job type: JitDiff, BenchmarkLibraries, or RegexDiff.")] string jobType,
        [Description("A git-compatible unified diff to apply to the tested branch.")] string patch,
        [Description(PatchJobRequest.ArgumentsDescription)] string arguments = null,
        [Description("Baseline GitHub repository in owner/name form.")] string baseRepository = "dotnet/runtime",
        [Description("Baseline branch to clone.")] string baseBranch = "main",
        CancellationToken cancellationToken = default)
    {
        return StartJobAsync(new PatchJobRequest
        {
            JobType = jobType,
            Patch = patch,
            Arguments = arguments,
            BaseRepository = baseRepository,
            BaseBranch = baseBranch,
        }, cancellationToken);
    }

    [McpServerTool(Name = "start_runtime_utils_commit_job", Title = "Start a runtime-utils job from a commit")]
    [Description(
        "Starts a JitDiff, BenchmarkLibraries, or RegexDiff job for a public GitHub non-merge commit, comparing it with its parent. " +
        "See https://gist.github.com/MihaZupan/6bcaacbe025265aa457ea7bb9a4dbcae for supported arguments and usage. " +
        "Without GitHub authentication the job uses a public Helix runner. An authorized GitHub caller may use Azure by " +
        "sending a GitHub access token with the bearer authentication scheme on the MCP HTTP request.")]
    public Task<PatchJobSubmissionResponse> StartCommitJobAsync(
        [Description("Job type: JitDiff, BenchmarkLibraries, or RegexDiff.")] string jobType,
        [Description("Public GitHub non-merge commit URL.")] string commit,
        [Description(PatchJobRequest.ArgumentsDescription)] string arguments = null,
        CancellationToken cancellationToken = default)
    {
        return StartJobAsync(new PatchJobRequest
        {
            JobType = jobType,
            Commit = commit,
            Arguments = arguments,
        }, cancellationToken);
    }

    [McpServerTool(Name = "get_runtime_utils_job_status", Title = "Get runtime-utils job status", Idempotent = true)]
    [Description("Returns the current state and result links for a runtime-utils job.")]
    public async Task<RuntimeUtilsJobStatusResponse> GetJobStatusAsync(
        [Description("The public job ID returned by start_runtime_utils_patch_job.")] string jobId,
        [Description("Whether to include retained logs in the response.")] bool includeLogs = false,
        [Description("Optionally include only the last N log lines. Supplying this also enables includeLogs.")] int? tail = null,
        CancellationToken cancellationToken = default)
    {
        return await RuntimeUtils.TryGetJobStatusAsync(jobId, cancellationToken, includeLogs, tail)
            ?? throw new KeyNotFoundException($"Runtime-utils job '{jobId}' was not found.");
    }

    private Task<PatchJobSubmissionResponse> StartJobAsync(PatchJobRequest request, CancellationToken cancellationToken)
    {
        HttpContext context = HttpContextAccessor.HttpContext;
        string githubToken = null;
        if (context?.Request.Headers.TryGetValue(HeaderNames.Authorization, out var authorization) == true &&
            authorization.Count == 1)
        {
            const string BearerPrefix = "Bearer ";
            string value = authorization[0];
            if (!value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Authorization must use the bearer authentication scheme.");
            }

            githubToken = value.Substring(BearerPrefix.Length).Trim();
            if (githubToken.Length == 0)
            {
                throw new UnauthorizedAccessException("The bearer authentication credential must not be empty.");
            }
        }

        return RuntimeUtils.StartPatchJobFromMcpAsync(request, githubToken, cancellationToken);
    }
}
