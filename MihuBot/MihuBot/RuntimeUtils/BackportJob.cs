using Octokit;
using System.Text.RegularExpressions;

namespace MihuBot.RuntimeUtils;

public sealed partial class BackportJob : JobBase
{
    public override string JobTitlePrefix => "Backport";

    protected override bool RunUsingGitHubActions => true;

    protected override bool PostErrorAsGitHubComment => ShouldLinkToPROrBranch;

    protected override string RepoOwner => "microsoft";
    protected override string RepoName => "reverse-proxy";

    public BackportJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    {
        Metadata.Add("MihuBotPushToken", parent.Configuration["GitHub:Token"]);
    }

    protected override Task InitializeAsync()
    {
        Match match = BackportBranchRegex().Match(CustomArguments);
        if (!match.Success)
        {
            throw new Exception("Invalid arguments. Expected `@MihuBot backport to release/latest`");
        }

        string targetBranch = match.Groups[1].Value;

        Metadata.Add("BackportJob_BaseRepo", $"{RepoOwner}/{RepoName}");
        Metadata.Add("BackportJob_ForkRepo", $"MihuBot/{RepoName}");
        Metadata.Add("BackportJob_TargetBranch", targetBranch);
        Metadata.Add("BackportJob_NewBranch", $"bp-{Rng.Snowflake()}");
        Metadata.Add("BackportJob_PatchUrl", PullRequest.PatchUrl);
        Metadata.Add("BackportJob_Title", $"Backport #{PullRequest.Number} to {targetBranch}");

        return Task.CompletedTask;
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

    [GeneratedRegex(@"^backport to ([a-zA-Z\d\/\.\-_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BackportBranchRegex();
}
