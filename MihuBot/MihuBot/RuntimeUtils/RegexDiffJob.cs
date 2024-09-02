using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class RegexDiffJob : JobBase
{
    public override string JobTitlePrefix => $"RegexDiff {Architecture}";

    protected override bool PostErrorAsGitHubComment => ShouldLinkToPROrBranch;

    private string _shortResultsMarkdown;
    private string _longResultsMarkdown;
    private string _jitDiffImprovements;
    private string _jitDiffRegressions;

    public RegexDiffJob(RuntimeUtilsService parent, string repository, string branch, string githubCommenterLogin, string arguments)
        : base(parent, repository, branch, githubCommenterLogin, arguments)
    { }

    public RegexDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await RunOnNewVirtualMachineAsync(defaultAzureCoreCount: 16, jobTimeout);

        string resultsMarkdown = string.Empty;
        if (!string.IsNullOrWhiteSpace(_shortResultsMarkdown))
        {
            resultsMarkdown =
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

        if (!string.IsNullOrEmpty(_jitDiffRegressions))
        {
            resultsMarkdown =
                $"""
                {resultsMarkdown}

                For a list of JIT diff regressions, see {await JitDiffJob.PostLargeDiffGistAsync(this, _jitDiffRegressions, regressions: true)}

                """;
        }

        if (!string.IsNullOrEmpty(_jitDiffImprovements))
        {
            resultsMarkdown =
                $"""
                {resultsMarkdown}

                For a list of JIT diff improvements, see {await JitDiffJob.PostLargeDiffGistAsync(this, _jitDiffImprovements, regressions: false)}

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
                RegexEntry[] entries = JsonSerializer.Deserialize<RegexEntry[]>(jsonFileStream)!;
                Console.WriteLine($"Working with {entries.Length} patterns");



                record KnownPattern(string Pattern, RegexOptions Options, int Count);

                sealed class RegexEntry
                {
                    public required KnownPattern Regex { get; set; }
                    public required string MainSource { get; set; }
                    public required string PrSource { get; set; }
                    public string? FullDiff { get; set; }
                    public string? ShortDiff { get; set; }
                    public string[]? SearchValuesOfChar { get; set; }
                    public (string[] Values, StringComparison ComparisonType)[]? SearchValuesOfString { get; set; }
                }
                ```

                </details>

                """;
        }

        string error = FirstErrorMessage is { } message
            ? $"\n```\n{message}\n```\n"
            : string.Empty;

        await UpdateIssueBodyAsync(
            $"""
            [Job]({ProgressDashboardUrl}) completed in {GetElapsedTime()}.
            {(ShouldLinkToPROrBranch ? TestedPROrBranchLink : "")}
            Using arguments: ````{CustomArguments}````
            {error}
            {resultsMarkdown}
            {GetArtifactList()}
            """);

        if (!string.IsNullOrEmpty(resultsMarkdown) &&
            ShouldLinkToPROrBranch &&
            ShouldMentionJobInitiator &&
            PullRequest is not null &&
            string.IsNullOrEmpty(error))
        {
            ShouldMentionJobInitiator = false;

            await Github.Issue.Comment.Create(DotnetRuntimeRepoOwner, DotnetRuntimeRepoName, PullRequest.Number, resultsMarkdown);
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 1024 * 1024, cancellationToken);
            string content = Encoding.UTF8.GetString(bytes);

            switch (fileName)
            {
                case "ShortExampleDiffs.md": _shortResultsMarkdown = content; break;
                case "LongExampleDiffs.md": _longResultsMarkdown = content; break;
                case "JitDiffImprovements.md": _jitDiffImprovements = content; break;
                case "JitDiffRegressions.md": _jitDiffRegressions = content; break;
            }

            return replacement;
        }

        return null;
    }
}
