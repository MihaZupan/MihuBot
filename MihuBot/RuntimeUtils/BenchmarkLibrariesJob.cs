using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class BenchmarkLibrariesJob : JobBase
{
    public override string JobTitlePrefix => $"Benchmark {Architecture}";

    protected override bool PostErrorAsGitHubComment => ShouldLinkToPROrBranch;

    private string _resultsMarkdown;

    public BenchmarkLibrariesJob(RuntimeUtilsService parent, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, githubCommenterLogin, arguments, comment)
    { }

    public BenchmarkLibrariesJob(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : base(parent, branch, githubCommenterLogin, arguments)
    { }

    public BenchmarkLibrariesJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: Metadata.ContainsKey("PrBranch") ? 8 : 4, jobTimeout);

        string resultsMarkdown = string.Empty;
        if (!string.IsNullOrWhiteSpace(_resultsMarkdown))
        {
            resultsMarkdown = _resultsMarkdown;

            if (resultsMarkdown.Length > CommentLengthLimit * 0.8)
            {
                var newGist = new NewGist
                {
                    Description = $"Benchmark results for {TrackingIssue?.HtmlUrl ?? ProgressDashboardUrl}",
                    Public = false,
                };

                newGist.Files.Add("Results.md", resultsMarkdown);

                Gist gist = await Github.Gist.Create(newGist);

                resultsMarkdown = $"See benchmark results at {gist.HtmlUrl}";
            }
        }

        await SetFinalTrackingIssueBodyAsync(resultsMarkdown);

        if (!string.IsNullOrEmpty(resultsMarkdown) &&
            ShouldLinkToPROrBranch &&
            ShouldMentionJobInitiator &&
            (PullRequest?.Number ?? GitHubComment?.IssueId) is int issueId)
        {
            await Github.Issue.Comment.Create(RepoOwner, RepoName, issueId, resultsMarkdown);
            ShouldMentionJobInitiator = false;
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName == "results.md")
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 1024 * 1024, cancellationToken);
            _resultsMarkdown = Encoding.UTF8.GetString(bytes);
            return replacement;
        }

        return null;
    }
}
