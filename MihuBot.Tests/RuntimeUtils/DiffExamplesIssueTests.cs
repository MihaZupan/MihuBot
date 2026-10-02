using MihuBot.RuntimeUtils;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class DiffExamplesIssueTests
{
    private const string ReportUrl = "https://mihubot.xyz/runtime-utils/test/diffs";

    [Fact]
    public void IssueSummaryIncludesAnalyzerOutputNotesAndBrowserLink()
    {
        var report = new DiffExamplesReport
        {
            Summary = "0 changed method listings.\n\nTotal bytes of base: 100\nTotal bytes of diff: 100",
            Notes = ["New method listings are excluded."],
            Entries = [],
        };

        string markdown = report.ToIssueSummaryMarkdown(ReportUrl);

        Assert.Contains(report.Summary, markdown, StringComparison.Ordinal);
        Assert.Contains(report.Notes[0], markdown, StringComparison.Ordinal);
        Assert.Contains(ReportUrl, markdown, StringComparison.Ordinal);
        Assert.True(markdown.IndexOf(ReportUrl, StringComparison.Ordinal) < markdown.IndexOf(report.Summary, StringComparison.Ordinal));
        Assert.Empty(report.GetIssueExampleComments(ReportUrl, 65_000));
    }

    [Fact]
    public void IssueExamplesIncludeAllJitCategoriesAndEscapeMethodNames()
    {
        var report = new DiffExamplesReport
        {
            Entries =
            [
                Entry("regression"),
                Entry("improvement"),
                Entry("same-size"),
            ],
        };

        string[] comments = report.GetIssueExampleComments(ReportUrl, 65_000).ToArray();

        Assert.Equal(3, comments.Length);
        Assert.Contains("JIT diff regressions", comments[0], StringComparison.Ordinal);
        Assert.Contains("JIT diff improvements", comments[1], StringComparison.Ordinal);
        Assert.Contains("Same-size JIT changes", comments[2], StringComparison.Ordinal);

        foreach (string comment in comments)
        {
            Assert.Contains("Method&lt;T&gt;", comment, StringComparison.Ordinal);
            Assert.Contains("Assembly: <code>Example.dasm</code>", comment, StringComparison.Ordinal);
            Assert.Contains("```diff\n-old\n+new\n```", comment, StringComparison.Ordinal);
            Assert.Contains("Showing 1 of 1 reported examples", comment, StringComparison.Ordinal);
            Assert.Contains(ReportUrl, comment, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void IssueExamplesPrioritizeLargestRelativeChangesAndLimitExampleCount()
    {
        var entries = Enumerable.Range(1, 30).Select(i =>
        {
            DiffExampleEntry entry = Entry("regression");
            entry.Method = $"Method{i}";
            entry.BaseBytes = 100;
            entry.DiffBytes = 100 + i;
            return entry;
        }).ToArray();
        var report = new DiffExamplesReport { Entries = entries };

        string comment = Assert.Single(report.GetIssueExampleComments(ReportUrl, 65_000));

        Assert.True(comment.IndexOf("Method30", StringComparison.Ordinal) < comment.IndexOf("Method29", StringComparison.Ordinal));
        Assert.DoesNotContain("<summary>Method10 ", comment, StringComparison.Ordinal);
        Assert.Contains("Showing 20 of 30 reported examples", comment, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueExamplesStayWithinGitHubLimitAndMarkTruncation()
    {
        var entries = Enumerable.Range(0, 20).Select(_ =>
        {
            DiffExampleEntry entry = Entry("regression");
            entry.Diff = new string('x', 128 * 1024);
            entry.Truncated = true;
            return entry;
        }).ToArray();
        var report = new DiffExamplesReport
        {
            Summary = new string('s', 100_000),
            Notes = [],
            Entries = entries,
        };

        string comment = Assert.Single(report.GetIssueExampleComments(ReportUrl, 65_000));

        Assert.True(comment.Length <= 65_000);
        Assert.Contains("Showing 3 of 20 reported examples", comment, StringComparison.Ordinal);
        Assert.Contains("truncated for GitHub", comment, StringComparison.Ordinal);
        Assert.Contains("truncated by the runner", comment, StringComparison.Ordinal);
        Assert.Contains(ReportUrl, comment, StringComparison.Ordinal);
        Assert.True(report.ToIssueSummaryMarkdown(ReportUrl).Length < 20_000);
        Assert.Contains("truncated for GitHub", report.ToIssueSummaryMarkdown(ReportUrl), StringComparison.Ordinal);
    }

    [Fact]
    public void IssueMarkdownUsesFencesLongerThanEmbeddedBackticks()
    {
        DiffExampleEntry entry = Entry("regression");
        entry.Diff = "-```\n+````";
        var report = new DiffExamplesReport
        {
            Summary = "Summary with ``` inside",
            Notes = [],
            Entries = [entry],
        };

        Assert.Contains("````\nSummary with ``` inside\n````", report.ToIssueSummaryMarkdown(ReportUrl), StringComparison.Ordinal);
        Assert.Contains("`````diff\n-```\n+````\n`````",
            Assert.Single(report.GetIssueExampleComments(ReportUrl, 65_000)), StringComparison.Ordinal);
    }

    [Fact]
    public void IssueExamplesSkipOversizedMetadataWithoutDroppingLaterExamples()
    {
        DiffExampleEntry oversized = Entry("regression");
        oversized.Method = new string('<', 128 * 1024);
        var report = new DiffExamplesReport { Entries = [oversized, Entry("regression")] };

        string comment = Assert.Single(report.GetIssueExampleComments(ReportUrl, 65_000));

        Assert.True(comment.Length <= 65_000);
        Assert.Contains("Method&lt;T&gt;", comment, StringComparison.Ordinal);
        Assert.Contains("Showing 1 of 2 reported examples", comment, StringComparison.Ordinal);
        Assert.Contains(ReportUrl, comment, StringComparison.Ordinal);
    }

    private static DiffExampleEntry Entry(string category) => new()
    {
        Assembly = "Example.dasm",
        Method = "Method<T>",
        Category = category,
        Description = "100 -> 101 bytes (+1)",
        Diff = "-old\n+new",
        BaseBytes = 100,
        DiffBytes = 101,
    };
}
