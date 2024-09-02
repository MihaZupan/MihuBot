﻿using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class JitDiffJob : JobBase
{
    public override string JobTitlePrefix => $"JitDiff {Architecture}";

    private string _frameworksDiffSummary;
    private string _shortDiffsImprovements;
    private string _shortDiffsRegressions;
    private string _longDiffsImprovements;
    private string _longDiffsRegressions;

    private bool ShouldPostDiffsComment => GetConfigFlag("ShouldPostDiffsComment", true);

    public JitDiffJob(RuntimeUtilsService parent, string repository, string branch, string githubCommenterLogin, string arguments)
        : base(parent, repository, branch, githubCommenterLogin, arguments)
    { }

    public JitDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 16, jobTimeout);

        LastSystemInfo = null;

        bool shouldHideDiffs = _frameworksDiffSummary?.Length > CommentLengthLimit / 2;

        string frameworksDiffs =
            $"### Diffs\n\n" +
            (shouldHideDiffs ? "<details>\n<summary>Diffs</summary>\n\n" : "") +
            $"```\n" +
            $"{_frameworksDiffSummary}\n" +
            $"```\n" +
            (shouldHideDiffs ? "\n</details>\n" : "") +
            $"\n\n";

        bool gotAnyDiffs = _frameworksDiffSummary is not null;

        await UpdateIssueBodyAsync(
            $$"""
            [Job]({{ProgressDashboardUrl}}) completed in {{GetElapsedTime()}}.
            {{(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}}

            {{(gotAnyDiffs ? frameworksDiffs : "")}}
            {{GetArtifactList()}}
            """);

        if (gotAnyDiffs && ShouldPostDiffsComment)
        {
            await PostDiffExamplesAsync(regressions: true);
            await PostDiffExamplesAsync(regressions: false);
        }
    }

    private async Task PostDiffExamplesAsync(bool regressions)
    {
        string shortDiffs = regressions ? _shortDiffsRegressions : _shortDiffsImprovements;
        string longDiffs = regressions ? _longDiffsRegressions : _longDiffsImprovements;

        if (!string.IsNullOrEmpty(shortDiffs))
        {
            if (!string.IsNullOrEmpty(longDiffs))
            {
                shortDiffs = $"{shortDiffs}\n\nLarger list of diffs: {await PostLargeDiffGistAsync(longDiffs, regressions)}";
            }

            await Github.Issue.Comment.Create(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, shortDiffs);
        }

        async Task<string> PostLargeDiffGistAsync(string diffsMarkdown, bool regressions)
        {
            var newGist = new NewGist
            {
                Description = $"JIT diffs {(regressions ? "regressions" : "improvements")} for {TrackingIssue.HtmlUrl}",
                Public = false
            };

            newGist.Files.Add(regressions ? "Regressions.md" : "Improvements.md", diffsMarkdown);

            Gist gist = await Github.Gist.Create(newGist);

            return gist.HtmlUrl;
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName.EndsWith(".md", StringComparison.Ordinal) ||
            fileName.EndsWith(".txt", StringComparison.Ordinal))
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
            }

            return replacement;
        }

        return null;
    }
}
