using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class RebaseJob : JobBase
{
    private string _jobTitle;
    public override string JobTitle => _jobTitle ??= $"[Rebase] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);

    public RebaseJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment) : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    {
        Metadata.Add("MihuBotPushToken", parent.Configuration["GitHub:Token"]);
    }

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

        await UpdateIssueBodyAsync(
            $"""
            [Job]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.
            {(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}
            {error}
            {GetArtifactList()}
            """);

        if (string.IsNullOrEmpty(error))
        {
            ShouldMentionJobInitiator = false;
        }
    }
}
