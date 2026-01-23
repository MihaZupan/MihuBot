using System.IO.Compression;
using System.Net.Mime;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils.Jobs;

public sealed class FuzzLibrariesJob : JobBase
{
    public override string JobTitlePrefix => $"Fuzzing {Architecture}";

    protected override bool PostErrorAsGitHubComment => ShouldLinkToPROrBranch;

    protected override bool RunUsingAzurePipelines => Fast;

    protected override bool RunUsingGitHubActions => !RunUsingAzurePipelines && !UseHelix;

    private readonly Dictionary<string, string> _errorStackTraces = [];
    private readonly Dictionary<string, string> _coverageReportUrls = [];

    public FuzzLibrariesJob(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : base(parent, branch, githubCommenterLogin, arguments)
    { }

    public FuzzLibrariesJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, CommentInfo comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await JobCompletionTcs.Task;

        string codeCoverageUrls = string.Empty;
        if (_coverageReportUrls.Where(e => e.Value is not null).ToArray() is { Length: > 0 } reportUrls)
        {
            codeCoverageUrls =
                $"""
                Code coverage reports:
                {string.Join('\n', reportUrls.Select(url => $"- [{url.Key}]({url.Value})"))}
                """;
        }

        string errorStackTraces = string.Empty;
        if (_errorStackTraces.Count > 0)
        {
            errorStackTraces = string.Join("\n\n", _errorStackTraces.Select(error =>
                $"""
                ```
                // {error.Key}
                {error.Value}
                ```
                """));

            errorStackTraces = $"\n{errorStackTraces}\n";
        }

        bool wasSuccessful = FirstErrorMessage is null && string.IsNullOrEmpty(errorStackTraces);

        await SetFinalTrackingIssueBodyAsync(
            $"""
            {errorStackTraces}
            {(wasSuccessful ? "Ran the fuzzer(s) successfully." : "")}

            {codeCoverageUrls}
            """);

        if ((!string.IsNullOrEmpty(errorStackTraces) || (wasSuccessful && !string.IsNullOrEmpty(codeCoverageUrls))) &&
            ShouldLinkToPROrBranch &&
            ShouldMentionJobInitiator &&
            PullRequest is not null)
        {
            string message;

            if (!string.IsNullOrEmpty(errorStackTraces))
            {
                string artifacts;
                lock (Artifacts)
                {
                    artifacts = string.Join('\n', _errorStackTraces
                        .Select(error => Artifacts.FirstOrDefault(a => a.FileName == $"{error.Key}-input.bin"))
                        .Where(error => error.Url is not null)
                        .Select(error => $"- [{error.FileName}]({error.Url}) ({GetRoughSizeString(error.Size)})"));
                }

                message =
                    $"""
                    {errorStackTraces}

                    {artifacts}
                    """;
            }
            else
            {
                message = $"Ran the fuzzer(s) successfully. {codeCoverageUrls}";
            }

            await Github.Issue.Comment.Create(RepoOwner, RepoName, PullRequest.Number, message);

            ShouldMentionJobInitiator = false;
        }

        if (string.IsNullOrEmpty(errorStackTraces) &&
            string.IsNullOrEmpty(FirstErrorMessage) &&
            GitHubComment is not null)
        {
            await GitHubComment.AddReactionAsync(Github, Octokit.ReactionType.Plus1);
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        const string StackNameSuffix = "-stack.txt";
        const string InputsNameSuffix = "-inputs.zip";
        const string CoverageNameSuffix = "-coverage.zip";

        if (fileName.EndsWith(StackNameSuffix, StringComparison.Ordinal))
        {
            string fuzzerName = fileName.Substring(0, fileName.Length - StackNameSuffix.Length);

            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 1024 * 1024, cancellationToken);
            string stackTrace = Encoding.UTF8.GetString(bytes);

            const int MaxLines = 60;

            if (stackTrace.SplitLines(removeEmpty: false) is { Length: > MaxLines } lines)
            {
                string truncatedMessage = $"... Skipped {lines.Length - MaxLines} lines ...";
                string marker = new('=', truncatedMessage.Length);
                lines = [.. lines.Take(MaxLines / 2), "", marker, truncatedMessage, marker, "", .. lines.TakeLast(MaxLines / 2)];
                stackTrace = string.Join('\n', lines);
            }

            lock (_errorStackTraces)
            {
                _errorStackTraces.Add(fuzzerName, stackTrace);
            }

            return replacement;
        }

        if (fileName.EndsWith(InputsNameSuffix, StringComparison.Ordinal))
        {
            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 64 * 1024 * 1024, cancellationToken);

            if (bytes.Length > 100_000)
            {
                try
                {
                    var blob = Parent.RunnerPersistentStateBlobContainerClient.GetBlobClient(fileName);

                    if (await blob.ExistsAsync(cancellationToken))
                    {
                        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);

                        if ((DateTimeOffset.UtcNow - properties.Value.LastModified).TotalDays < 1 &&
                            properties.Value.ContentLength > bytes.Length)
                        {
                            return replacement;
                        }

                        await blob.DeleteAsync(cancellationToken: cancellationToken);
                    }

                    await blob.UploadAsync(BinaryData.FromBytes(bytes), cancellationToken);
                }
                catch (Exception ex)
                {
                    string message = $"Failed to update inputs blob:\n\n```\n{ex}\n```";
                    Log(message);
                    await Logger.DebugAsync(message);
                }
            }

            return replacement;
        }

        if (fileName.EndsWith(CoverageNameSuffix, StringComparison.Ordinal))
        {
            string fuzzerName = fileName.Substring(0, fileName.Length - CoverageNameSuffix.Length);

            lock (_coverageReportUrls)
            {
                if (_coverageReportUrls.Count > 10 || !_coverageReportUrls.TryAdd(fuzzerName, null))
                {
                    return null;
                }
            }

            (byte[] bytes, Stream replacement) = await ReadArtifactAndReplaceStreamAsync(contentStream, 64 * 1024 * 1024, cancellationToken);

            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Sum(e => e.Length) > 128 * 1024 * 1024)
            {
                return replacement;
            }

            (string Name, byte[] Data)[] htmlEntries = [.. zip.Entries
                .Where(e => e.FullName.StartsWith("html/", StringComparison.Ordinal))
                .Select(e => (e.Name, e.ToArray()))];

            try
            {
                string indexUrl = null;
                await Parallel.ForEachAsync(htmlEntries, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken }, async (entry, ct) =>
                {
                    BlobClient blob = Parent.ArtifactsBlobContainerClient.GetBlobClient($"{ExternalId}/fuzzing-coverage/{entry.Name}");

                    var options = new BlobUploadOptions
                    {
                        AccessTier = AccessTier.Hot,
                        HttpHeaders = new BlobHttpHeaders { ContentType = MediaTypeMap.GetMediaType(entry.Name) }
                    };
                    await blob.UploadAsync(new BinaryData(entry.Data), options, ct);

                    if (entry.Name == "index.html")
                    {
                        indexUrl = blob.Uri.AbsoluteUri;
                    }
                });

                lock (_coverageReportUrls)
                {
                    _coverageReportUrls[fuzzerName] = indexUrl;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to upload coverage report blobs:\n\n```\n{ex}\n```");
            }

            return replacement;
        }

        return null;
    }
}
