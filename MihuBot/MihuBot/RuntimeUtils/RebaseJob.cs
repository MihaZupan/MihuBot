using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class RebaseJob : JobBase
{
    private string _jobTitle;
    public override string JobTitle => _jobTitle ??= $"[{JobName}] [{PullRequest.User.Login}] {PullRequest.Title}".TruncateWithDotDotDot(99);

    private string JobName =>
        CustomArguments.StartsWith("rebase", StringComparison.OrdinalIgnoreCase) ? "Rebase" :
        CustomArguments.StartsWith("merge", StringComparison.OrdinalIgnoreCase) ? "Merge" :
        "Format";

    protected override bool RunUsingGitHubActions => true;

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
