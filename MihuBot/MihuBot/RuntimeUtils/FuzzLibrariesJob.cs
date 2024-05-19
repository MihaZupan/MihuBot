using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class FuzzLibrariesJob : JobBase
{
    private string _jobTitle;
    public override string JobTitle => _jobTitle ??= $"[Fuzzing] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);

    protected override bool PostErrorAsGitHubComment => true;

    private string _errorStackTrace;

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
        await JobCompletionTcs.Task;

        string error = FirstErrorMessage is { } message
            ? $"\n```\n{message}\n```\n"
            : string.Empty;

        string errorStackTrace = _errorStackTrace is { } stackTrace
            ? $"\n```\n{stackTrace}\n```\n"
            : string.Empty;

        await UpdateIssueBodyAsync(
            $"""
            [Job]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.
            {(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}
            {error}
            {errorStackTrace}
            {GetArtifactList()}
            """);

        if (!string.IsNullOrEmpty(errorStackTrace) &&
            ShouldLinkToPROrBranch &&
            ShouldMentionJobInitiator &&
            PullRequest is not null)
        {
            (string FileName, string Url, long Size) input = default;
            lock (Artifacts)
            {
                input = Artifacts.Where(a => a.FileName.EndsWith("-input.bin", StringComparison.Ordinal)).FirstOrDefault();
            }

            if (input.Url is not null)
            {
                ShouldMentionJobInitiator = false;

                await Github.Issue.Comment.Create(DotnetRuntimeRepoOwner, DotnetRuntimeRepoName, PullRequest.Number,
                    $"""
                    {errorStackTrace}

                    [{input.FileName}]({input.Url}) ({GetRoughSizeString(input.Size)})
                    """);
            }
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName == "stack.txt")
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 128 * 1024, cancellationToken);
            _errorStackTrace = Encoding.UTF8.GetString(bytes);
            return replacement;
        }

        return null;
    }
}
