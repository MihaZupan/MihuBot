using System.Text.RegularExpressions;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed partial class RegexDiffJob : JobBase
{
    public override string JobTitlePrefix => $"RegexDiff {Architecture}";

    protected override bool PostErrorAsGitHubComment => ShouldLinkToPROrBranch;

    public RegexDiffJob(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : base(parent, branch, githubCommenterLogin, arguments)
    { }

    public RegexDiffJob(RuntimeUtilsService parent, string baseRepository, string baseBranch, string baseCommit, string patchContent, string testedLink, string githubCommenterLogin, string arguments, bool forceHelix)
        : base(parent, baseRepository, baseBranch, baseCommit, patchContent, testedLink, githubCommenterLogin, arguments, forceHelix)
    { }

    public RegexDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        if (await TrySignalAvailableRunnerAsync())
        {
            await JobCompletionTcs.Task;
        }
        else
        {
            await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 16, jobTimeout);
        }

        string resultsMarkdown = string.Empty;

        if (TryFindLogLine(line => ChangedPatternStatusLineRegex().IsMatch(line)) is { } line)
        {
            resultsMarkdown = $"{ChangedPatternStatusLineRegex().Match(line).Groups[1].ValueSpan}\n\n";
        }

        if (HasDiffExamples)
        {
            resultsMarkdown += $"[Browse JIT and generated regex source diff examples]({DiffExamplesUrl})\n\n";
        }

        string error = FirstErrorMessage is { } message
            ? $"\n```\n{message}\n```\n"
            : string.Empty;

        await SetFinalTrackingIssueBodyAsync(resultsMarkdown);

        if (!string.IsNullOrEmpty(resultsMarkdown) &&
            ShouldLinkToPROrBranch &&
            ShouldMentionJobInitiator &&
            PullRequest is not null &&
            string.IsNullOrEmpty(error))
        {
            ShouldMentionJobInitiator = false;

            await Github.Issue.Comment.Create(RepoOwner, RepoName, PullRequest.Number, resultsMarkdown);
        }
    }

    // NOTE: 42 out of 123 patterns have generated source code changes.
    [GeneratedRegex(@"NOTE: (\d+ out of \d+ patterns have generated source code changes\.)$")]
    private static partial Regex ChangedPatternStatusLineRegex();
}
