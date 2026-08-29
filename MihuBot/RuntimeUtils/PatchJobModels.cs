using System.ComponentModel;

namespace MihuBot.RuntimeUtils;

public sealed class PatchJobRequest
{
    public const string ArgumentsDescription =
        "Additional runtime-utils command-line arguments. For BenchmarkLibraries, use 'benchmark <filter>'; benchmark jobs " +
        "may take a long time depending on the filter and options. '-medium' is recommended for more stable results, but a " +
        "broad filter may then exceed the job time limit. Machine controls include '-arm' for ARM64, '-intel' for Intel " +
        "Azure/Hetzner hardware, '-hetzner', and '-helix'. With '-helix', '-queue <queueId>' selects a specific compatible " +
        "queue; for example, 'ubuntu.2404.armarch.open' selects a different ARM64 environment. macOS Helix queues are not " +
        "currently supported because the runtime-utils runner requires Linux or Windows.";

    [Description("Job type: JitDiff, BenchmarkLibraries, or RegexDiff.")]
    public string JobType { get; set; }

    [Description("A git-compatible unified diff to apply to the tested branch. Specify either Patch or Commit.")]
    public string Patch { get; set; }

    [Description("A public GitHub non-merge commit URL to compare with its parent. Specify either Commit or Patch.")]
    public string Commit { get; set; }

    [Description(ArgumentsDescription)]
    public string Arguments { get; set; }

    [Description("GitHub repository containing the baseline branch.")]
    public string BaseRepository { get; set; } = "dotnet/runtime";

    [Description("Baseline branch to clone before applying the patch.")]
    public string BaseBranch { get; set; } = "main";
}

public sealed record PatchJobSubmissionResponse(
    string JobId,
    string StatusUrl,
    string ProgressUrl,
    string LogsUrl,
    string RunnerPolicy);

public sealed record RuntimeUtilsJobStatusResponse(
    string JobId,
    string State,
    string Title,
    DateTime StartedAt,
    TimeSpan Duration,
    string ProgressUrl,
    string LogsUrl,
    string TrackingIssueUrl,
    string ProgressSummary,
    string Error,
    CompletedJobRecord.Artifact[] Artifacts,
    string[] Logs);

public sealed class RuntimeUtilsCapacityException(string message) : Exception(message);

public sealed class RuntimeUtilsSubmissionsDisabledException : Exception
{
    public RuntimeUtilsSubmissionsDisabledException()
        : base("Runtime-utils API and MCP job submissions are currently disabled.")
    {
    }
}
