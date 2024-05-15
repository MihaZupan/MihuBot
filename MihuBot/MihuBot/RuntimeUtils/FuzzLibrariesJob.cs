using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class FuzzLibrariesJob : JobBase
{
    private string _jobTitle;
    public override string JobTitle => _jobTitle ??= $"[Fuzzing] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);

    protected override bool PostErrorAsGitHubComment => true;

    public FuzzLibrariesJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments)
        : base(parent, pullRequest, githubCommenterLogin, arguments)
    { }

    protected override string GetInitialIssueBody()
    {
        return
            $"""
            Job is in progress - see {ProgressDashboardUrl}
            {(FromGithubComment && ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}

            <!-- RUN_AS_GITHUB_ACTION_{ExternalId} -->
            """;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await JobCompletionTcs.Task;

        string error = FirstErrorMessage is { } message
            ? $"\n```\n{message}\n```\n"
            : string.Empty;

        await UpdateIssueBodyAsync(
            $"""
            [Job]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.
            {error}
            {GetArtifactList()}
            """);
    }
}
