using Azure.Storage.Sas;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils.Jobs;

public sealed class JitDiffJob : JobBase
{
    public override string JobTitlePrefix => $"JitDiff {Architecture}";

    private string _frameworksDiffSummary;
    private string _shortDiffsImprovements;
    private string _shortDiffsRegressions;
    private string _longDiffsImprovements;
    private string _longDiffsRegressions;

    private bool ShouldPostDiffsComment => GetConfigFlag("ShouldPostDiffsComment", true);

    public JitDiffJob(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : base(parent, branch, githubCommenterLogin, arguments)
    { }

    public JitDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override Task InitializeAsync(CancellationToken jobTimeout)
    {
        var containerClient = Parent.JitDiffExtraAssembliesBlobContainerClient;
        Uri sasUri = containerClient.GenerateSasUri(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List, DateTimeOffset.UtcNow.Add(MaxJobDuration));
        Metadata.Add("JitDiffExtraAssembliesSasUri", sasUri.AbsoluteUri);

        return Task.CompletedTask;
    }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 16, jobTimeout);

        LastSystemInfo = null;

        bool shouldHideDiffs = _frameworksDiffSummary?.Length > CommentLengthLimit / 6;

        string frameworksDiffs =
            $"### Diffs\n\n" +
            (shouldHideDiffs ? "<details>\n<summary>Diffs</summary>\n\n" : "") +
            $"```\n" +
            $"{_frameworksDiffSummary}\n" +
            $"```\n" +
            (shouldHideDiffs ? "\n</details>\n" : "") +
            $"\n\n";

        bool gotAnyDiffs = _frameworksDiffSummary is not null;

        await SetFinalTrackingIssueBodyAsync(gotAnyDiffs ? frameworksDiffs : "");

        if (gotAnyDiffs && ShouldPostDiffsComment && TrackingIssue is not null)
        {
            await PostDiffExamplesAsync(regressions: true, _shortDiffsRegressions, _longDiffsRegressions);
            await PostDiffExamplesAsync(regressions: false, _shortDiffsImprovements, _longDiffsImprovements);
        }
    }

    private async Task PostDiffExamplesAsync(bool regressions, string shortDiffs, string longDiffs)
    {
        if (!string.IsNullOrEmpty(shortDiffs))
        {
            if (!string.IsNullOrEmpty(longDiffs))
            {
                shortDiffs = $"{shortDiffs}\n\nLarger list of diffs: {await PostLargeDiffGistAsync(this, longDiffs, regressions)}";
            }

            await Github.Issue.Comment.Create(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, shortDiffs);
        }
    }

    public static async Task<string> PostLargeDiffGistAsync(JobBase job, string diffsMarkdown, bool regressions)
    {
        var newGist = new NewGist
        {
            Description = $"JIT diffs {(regressions ? "regressions" : "improvements")} for {job.TrackingIssue?.HtmlUrl ?? job.ProgressDashboardUrl}",
            Public = false
        };

        newGist.Files.Add(regressions ? "Regressions.md" : "Improvements.md", diffsMarkdown);

        Gist gist = await job.Github.Gist.Create(newGist);

        return gist.HtmlUrl;
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName == "diff-frameworks.txt" || fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 1024 * 1024, cancellationToken);
            string content = Encoding.UTF8.GetString(bytes);

            switch (fileName)
            {
                case "diff-frameworks.txt": _frameworksDiffSummary = content; break;
                case "ShortDiffsImprovements.md": _shortDiffsImprovements = content; break;
                case "ShortDiffsRegressions.md": _shortDiffsRegressions = content; break;
                case "LongDiffsImprovements.md": _longDiffsImprovements = content; break;
                case "LongDiffsRegressions.md": _longDiffsRegressions = content; break;
                default: break;
            }

            return replacement;
        }

        return null;
    }
}
