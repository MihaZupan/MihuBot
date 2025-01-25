using Octokit;
using System.Text.RegularExpressions;

namespace MihuBot.RuntimeUtils;

public sealed partial class RegexDiffJob : JobBase
{
    public override string JobTitlePrefix => $"RegexDiff {Architecture}";

    protected override bool PostErrorAsGitHubComment => ShouldLinkToPROrBranch;

    private string _shortResultsMarkdown;
    private string _longResultsMarkdown;
    private string _jitAnalyzeSummary;
    private string _jitDiffImprovements;
    private string _jitDiffRegressions;

    public RegexDiffJob(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : base(parent, branch, githubCommenterLogin, arguments)
    { }

    public RegexDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 16, jobTimeout);

        string resultsMarkdown = string.Empty;

        if (TryFindLogLine(line => ChangedPatternStatusLineRegex().IsMatch(line)) is { } line)
        {
            resultsMarkdown = $"{ChangedPatternStatusLineRegex().Match(line).Groups[1].ValueSpan}\n\n";
        }

        if (!string.IsNullOrWhiteSpace(_shortResultsMarkdown))
        {
            resultsMarkdown +=
                $"""
                <details>
                <summary>Examples of GeneratedRegex source diffs</summary>

                {_shortResultsMarkdown}
                """;

            if (!string.IsNullOrWhiteSpace(_longResultsMarkdown))
            {
                var newGist = new NewGist
                {
                    Description = $"Regex source generator diff examples for {TrackingIssue.HtmlUrl}",
                    Public = false,
                };

                newGist.Files.Add("Results.md", _longResultsMarkdown);

                Gist gist = await Github.Gist.Create(newGist);

                resultsMarkdown =
                    $"""
                    {resultsMarkdown}

                    For more diff examples, see {gist.HtmlUrl}
                    """;
            }

            resultsMarkdown =
                $"""
                {resultsMarkdown}

                </details>

                """;
        }

        if (!string.IsNullOrEmpty(_jitAnalyzeSummary))
        {
            string summary = _jitAnalyzeSummary
                .ReplaceLineEndings("\n");

            int offset = summary.IndexOf("\n\n\n", StringComparison.Ordinal);
            if (offset >= 0)
            {
                summary = summary.Substring(0, offset);
                offset = summary.IndexOf("Total bytes of base", StringComparison.Ordinal);
                if (offset >= 0)
                {
                    summary = summary.Substring(offset);
                    summary = summary.Trim();

                    resultsMarkdown =
                        $"""
                        {resultsMarkdown}

                        ```
                        {summary}
                        ```
                        """;
                }
            }
        }

        if (!string.IsNullOrEmpty(_jitDiffRegressions))
        {
            resultsMarkdown =
                $"""
                {resultsMarkdown}
                For a list of JIT diff regressions, see [Regressions.md]({await JitDiffJob.PostLargeDiffGistAsync(this, _jitDiffRegressions, regressions: true)})
                """;
        }

        if (!string.IsNullOrEmpty(_jitDiffImprovements))
        {
            resultsMarkdown =
                $"""
                {resultsMarkdown}
                For a list of JIT diff improvements, see [Improvements.md]({await JitDiffJob.PostLargeDiffGistAsync(this, _jitDiffImprovements, regressions: false)})
                """;
        }

        if (Artifacts.FirstOrDefault(a => a.FileName == "Results.zip") is { } allResultsArchive)
        {
            resultsMarkdown =
                $$"""
                {{resultsMarkdown}}

                <details>
                <summary>Sample source code for further analysis</summary>

                ```c#
                const string JsonPath = "RegexResults-{{TrackingIssue?.Number}}.json";
                if (!File.Exists(JsonPath))
                {
                    await using var archiveStream = await new HttpClient().GetStreamAsync("{{allResultsArchive.Url}}");
                    using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
                    archive.Entries.First(e => e.Name == "Results.json").ExtractToFile(JsonPath);
                }

                using FileStream jsonFileStream = File.OpenRead(JsonPath);
                RegexEntry[] entries = JsonSerializer.Deserialize<RegexEntry[]>(jsonFileStream, new JsonSerializerOptions { IncludeFields = true })!;
                Console.WriteLine($"Working with {entries.Length} patterns");



                record KnownPattern(string Pattern, RegexOptions Options, int Count);

                sealed class RegexEntry
                {
                    public required KnownPattern Regex { get; set; }
                    public required string MainSource { get; set; }
                    public required string PrSource { get; set; }
                    public string? FullDiff { get; set; }
                    public string? ShortDiff { get; set; }
                    public (string Name, string Values)[]? SearchValuesOfChar { get; set; }
                    public (string[] Values, StringComparison ComparisonType)[]? SearchValuesOfString { get; set; }
                }
                ```

                </details>

                """;
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

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName == "JitAnalyzeSummary.txt" || fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            if (fileName.StartsWith("LongJitDiff", StringComparison.Ordinal))
            {
                return null;
            }

            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 1024 * 1024, cancellationToken);
            string content = Encoding.UTF8.GetString(bytes);

            switch (fileName)
            {
                case "JitAnalyzeSummary.txt": _jitAnalyzeSummary = content; break;
                case "ShortExampleDiffs.md": _shortResultsMarkdown = content; break;
                case "LongExampleDiffs.md": _longResultsMarkdown = content; break;
                case "JitDiffImprovements.md": _jitDiffImprovements = content; break;
                case "JitDiffRegressions.md": _jitDiffRegressions = content; break;
            }

            return replacement;
        }

        return null;
    }

    // NOTE: 42 out of 123 patterns have generated source code changes.
    [GeneratedRegex(@"NOTE: (\d+ out of \d+ patterns have generated source code changes\.)$")]
    private static partial Regex ChangedPatternStatusLineRegex();
}
