﻿using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils.Jobs;

public sealed class RebaseJob : JobBase
{
    public override string JobTitlePrefix =>
        CustomArguments.StartsWith("rebase", StringComparison.OrdinalIgnoreCase) ? "Rebase" :
        CustomArguments.StartsWith("merge", StringComparison.OrdinalIgnoreCase) ? "Merge" :
        "Format";

    protected override bool RunUsingGitHubActions => true;

    public RebaseJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    {
        Metadata.Add("MihuBotPushToken", parent.Configuration["GitHub:Token"]);
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await JobCompletionTcs.Task;

        await SetFinalTrackingIssueBodyAsync();

        if (FirstErrorMessage is null)
        {
            ShouldMentionJobInitiator = false;
        }
    }
}
