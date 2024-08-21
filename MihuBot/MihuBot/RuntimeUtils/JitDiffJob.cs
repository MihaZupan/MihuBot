using Octokit;
using System.IO.Compression;

namespace MihuBot.RuntimeUtils;

public sealed class JitDiffJob : JobBase
{
    public override string JobTitlePrefix => $"JitDiff {Architecture}";

    private string _frameworksDiffSummary;
    private readonly TempFile _frameworksDiffsZipFile = new("zip");
    private readonly Dictionary<(string DasmFile, bool Main), TempFile> _frameworksDiffFiles = new();

    private bool IncludeKnownNoise => CustomArguments.Contains("-includeKnownNoise", StringComparison.OrdinalIgnoreCase);
    private bool IncludeNewMethodRegressions => CustomArguments.Contains("-includeNewMethodRegressions", StringComparison.OrdinalIgnoreCase);
    private bool IncludeRemovedMethodImprovements => CustomArguments.Contains("-includeRemovedMethodImprovements", StringComparison.OrdinalIgnoreCase);

    private bool ShouldPostDiffsComment => GetConfigFlag("ShouldPostDiffsComment", true);

    public JitDiffJob(RuntimeUtilsService parent, string repository, string branch, string githubCommenterLogin, string arguments)
        : base(parent, repository, branch, githubCommenterLogin, arguments)
    { }

    public JitDiffJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        try
        {
            await RunOnNewVirtualMachineAsync(16, jobTimeout);

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
                await PostDiffExamplesAsync();
            }
        }
        finally
        {
            _frameworksDiffsZipFile.Dispose();

            foreach (TempFile file in _frameworksDiffFiles.Values)
            {
                file.Dispose();
            }
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        if (fileName == "diff-frameworks.txt")
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 128 * 1024, cancellationToken);
            _frameworksDiffSummary = Encoding.UTF8.GetString(bytes);
            return replacement;
        }

        if (fileName == "jit-diffs-frameworks.zip")
        {
            LogsReceived("Saving jit-diffs-frameworks.zip");

            await using (var fs = File.OpenWrite(_frameworksDiffsZipFile.Path))
            {
                await contentStream.CopyToAsync(fs, cancellationToken);
            }

            return File.OpenRead(_frameworksDiffsZipFile.Path);
        }

        return null;
    }

    private async Task PostDiffExamplesAsync()
    {
        try
        {
            await ExtractFrameworksDiffsZipAsync();

            await PostDiffExamplesAsync(regressions: true);
            await PostDiffExamplesAsync(regressions: false);
        }
        catch (Exception ex)
        {
            Logger.DebugLog($"Failed to post diff examples: {ex}");
        }

        async Task PostDiffExamplesAsync(bool regressions)
        {
            var allChanges = await GetDiffMarkdownAsync(JitDiffUtils.ParseDiffEntries(_frameworksDiffSummary, regressions));

            string changes = JitDiffUtils.GetCommentMarkdown(allChanges.Diffs, CommentLengthLimit, regressions, out bool truncated);

            Logger.DebugLog($"Found {allChanges.Diffs.Length} changes, comment length={changes.Length} for {nameof(regressions)}={regressions}");

            if (changes.Length != 0)
            {
                if (allChanges.NoisyDiffsRemoved)
                {
                    changes = $"{changes}\n\nNote: some changes were skipped as they were likely noise.";
                }

                if (truncated)
                {
                    changes = $"{changes}\n\nLarger list of diffs: {await PostLargeDiffGistAsync(allChanges.Diffs, regressions)}";
                }

                await Github.Issue.Comment.Create(IssueRepositoryOwner, IssueRepositoryName, TrackingIssue.Number, changes);
            }
        }

        async Task<string> PostLargeDiffGistAsync(string[] diffs, bool regressions)
        {
            var newGist = new NewGist
            {
                Description = $"JIT diffs {(regressions ? "regressions" : "improvements")} for {TrackingIssue.HtmlUrl}",
                Public = false
            };

            const int GistLengthLimit = 900 * 1024;

            string md = JitDiffUtils.GetCommentMarkdown(diffs, GistLengthLimit, regressions, out _);

            newGist.Files.Add(regressions ? "Regressions.md" : "Improvements.md", md);

            Gist gist = await Github.Gist.Create(newGist);

            return gist.HtmlUrl;
        }

        async Task<(string[] Diffs, bool NoisyDiffsRemoved)> GetDiffMarkdownAsync((string Description, string DasmFile, string Name)[] diffs)
        {
            if (diffs.Length == 0)
            {
                return (Array.Empty<string>(), false);
            }

            bool noisyMethodsRemoved = false;
            bool includeKnownNoise = IncludeKnownNoise;
            bool includeRemovedMethod = IncludeRemovedMethodImprovements;
            bool IncludeNewMethod = IncludeNewMethodRegressions;

            var result = await diffs
                .ToAsyncEnumerable()
                .Where(diff => includeRemovedMethod || !IsRemovedMethod(diff.Description))
                .Where(diff => IncludeNewMethod || !IsNewMethod(diff.Description))
                .SelectAwait(async diff =>
                {
                    if (!_frameworksDiffFiles.TryGetValue((diff.DasmFile, Main: true), out TempFile mainDiffsFile) ||
                        !_frameworksDiffFiles.TryGetValue((diff.DasmFile, Main: false), out TempFile prDiffsFile))
                    {
                        return string.Empty;
                    }

                    LogsReceived($"Generating diffs for {diff.Name}");

                    StringBuilder sb = new();

                    sb.AppendLine("<details>");
                    sb.AppendLine($"<summary>{diff.Description} - {diff.Name}</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```diff");

                    using var baseFile = new TempFile("txt");
                    using var prFile = new TempFile("txt");

                    await File.WriteAllTextAsync(baseFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(mainDiffsFile.Path, diff.Name));
                    await File.WriteAllTextAsync(prFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(prDiffsFile.Path, diff.Name));

                    List<string> lines = new();
                    await ProcessHelper.RunProcessAsync("git", $"diff --minimal --no-index -U20000 {baseFile} {prFile}", lines);

                    if (lines.Count == 0)
                    {
                        return string.Empty;
                    }
                    else
                    {
                        foreach (string line in lines)
                        {
                            if (ShouldSkipLine(line.AsSpan().TrimStart()))
                            {
                                continue;
                            }

                            if (!includeKnownNoise && LineIsIndicativeOfKnownNoise(line.AsSpan().TrimStart()))
                            {
                                noisyMethodsRemoved = true;
                                return string.Empty;
                            }

                            sb.AppendLine(line);
                        }
                    }

                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();

                    string result = sb.ToString();

                    Logger.DebugLog($"Generated diff for '{diff.Name}':\n{result}");

                    return result;
                })
                .Where(diff => !string.IsNullOrEmpty(diff))
                .Take(20)
                .ToArrayAsync();

            return (result, noisyMethodsRemoved);

            static bool IsRemovedMethod(ReadOnlySpan<char> description) =>
                description.Contains("-100.", StringComparison.Ordinal);

            static bool IsNewMethod(ReadOnlySpan<char> description) =>
                description.Contains("∞ of base", StringComparison.Ordinal) ||
                description.Contains("Infinity of base", StringComparison.Ordinal);

            static bool ShouldSkipLine(ReadOnlySpan<char> line)
            {
                return
                    line.StartsWith("diff --git", StringComparison.Ordinal) ||
                    line.StartsWith("index ", StringComparison.Ordinal) ||
                    line.StartsWith("+++", StringComparison.Ordinal) ||
                    line.StartsWith("---", StringComparison.Ordinal) ||
                    line.StartsWith("@@", StringComparison.Ordinal) ||
                    line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal) ||
                    line.StartsWith("; ============================================================", StringComparison.Ordinal);
            }

            static bool LineIsIndicativeOfKnownNoise(ReadOnlySpan<char> line)
            {
                if (line.IsEmpty || line[0] is not ('+' or '-'))
                {
                    return false;
                }

                return
                    line.Contains("CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS", StringComparison.Ordinal) ||
                    line.Contains("ProcessorIdCache:RefreshCurrentProcessorId", StringComparison.Ordinal) ||
                    line.Contains("Interop+Sys:SchedGetCpu()", StringComparison.Ordinal);
            }
        }

        async Task ExtractFrameworksDiffsZipAsync()
        {
            Stopwatch extractTime = Stopwatch.StartNew();
            LogsReceived("Extracting Frameworks diffs zip ...");

            await using Stream zipStream = File.OpenRead(_frameworksDiffsZipFile.Path);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName;
                string dasmFile = Path.GetFileName(name);

                if (!dasmFile.EndsWith(".dasm", StringComparison.Ordinal) ||
                    !name.Contains("dasmset_", StringComparison.Ordinal))
                {
                    continue;
                }

                bool isMain = name.Replace('\\', '/').Contains("/main/", StringComparison.Ordinal);

                var tempFile = new TempFile("txt");
                _frameworksDiffFiles.Add((dasmFile, isMain), tempFile);

                entry.ExtractToFile(tempFile.Path, overwrite: true);
            }

            LogsReceived($"Finished extracting Frameworks diffs zip in {extractTime.Elapsed.TotalSeconds:N1} seconds");
        }
    }
}
