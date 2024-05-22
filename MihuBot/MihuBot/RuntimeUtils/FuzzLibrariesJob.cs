using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class FuzzLibrariesJob : JobBase
{
    private string _jobTitle;
    public override string JobTitle => _jobTitle ??= $"[Fuzzing] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);

    protected override bool PostErrorAsGitHubComment => true;

    private readonly Dictionary<string, string> _errorStackTraces = new();

    public FuzzLibrariesJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments)
        : base(parent, pullRequest, githubCommenterLogin, arguments)
    { }

    protected override string GetInitialIssueBody()
    {
        return
            $"""
            Job is in progress - see {ProgressDashboardUrl}
            {(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}

            <!-- RUN_AS_GITHUB_ACTION_{ExternalId} -->
            """;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        LogsReceived("Starting runner on GitHub actions ...");

        await JobCompletionTcs.Task;

        string error = FirstErrorMessage is { } message
            ? $"\n```\n{message}\n```\n"
            : string.Empty;

        string errorStackTraces = string.Empty;
        if (_errorStackTraces.Count > 0)
        {
            errorStackTraces = string.Join("\n\n", _errorStackTraces.Select(error =>
                $"""
                ```
                // {error.Key}
                {error.Value}
                ```
                """));

            errorStackTraces = $"\n{errorStackTraces}\n";
        }

        await UpdateIssueBodyAsync(
            $"""
            [Job]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.
            {(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}
            {error}
            {errorStackTraces}
            {GetArtifactList()}
            """);

        if (!string.IsNullOrEmpty(errorStackTraces) &&
            ShouldLinkToPROrBranch &&
            ShouldMentionJobInitiator &&
            PullRequest is not null)
        {
            ShouldMentionJobInitiator = false;

            string artifacts;
            lock (Artifacts)
            {
                artifacts = string.Join('\n', _errorStackTraces
                    .Select(error => Artifacts.Where(a => a.FileName == $"{error.Key}-input.bin").FirstOrDefault())
                    .Where(error => error.Url is not null)
                    .Select(error => $"- [{error.FileName}]({error.Url}) ({GetRoughSizeString(error.Size)})"));
            }

            await Github.Issue.Comment.Create(DotnetRuntimeRepoOwner, DotnetRuntimeRepoName, PullRequest.Number,
                $"""
                {errorStackTraces}

                {artifacts}
                """);
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        const string NameSuffix = "-stack.txt";

        if (fileName.EndsWith(NameSuffix, StringComparison.Ordinal))
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 1024 * 1024, cancellationToken);
            string stackTrace = Encoding.UTF8.GetString(bytes);

            const int MaxLines = 60;

            if (stackTrace.SplitLines(removeEmpty: false) is { Length: > MaxLines } lines)
            {
                string truncatedMessage = $"... Skipped {lines.Length - MaxLines} lines ...";
                string marker = new('=', truncatedMessage.Length);
                lines = [.. lines.Take(MaxLines / 2), "", marker, truncatedMessage, marker, "", .. lines.TakeLast(MaxLines / 2)];
                stackTrace = string.Join('\n', lines);
            }

            lock (_errorStackTraces)
            {
                _errorStackTraces.Add(fileName.Substring(0, fileName.Length - NameSuffix.Length), stackTrace);
            }

            return replacement;
        }

        return null;
    }
}
