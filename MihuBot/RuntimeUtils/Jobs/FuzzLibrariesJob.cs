using Octokit;

namespace MihuBot.RuntimeUtils.Jobs;

public sealed class FuzzLibrariesJob : JobBase
{
    public override string JobTitlePrefix => "Fuzzing";

    protected override bool PostErrorAsGitHubComment => ShouldLinkToPROrBranch;

    protected override bool RunUsingAzurePipelines => Fast;

    protected override bool RunUsingGitHubActions => !RunUsingAzurePipelines;

    private readonly Dictionary<string, string> _errorStackTraces = new();

    public FuzzLibrariesJob(RuntimeUtilsService parent, BranchReference branch, string githubCommenterLogin, string arguments)
        : base(parent, branch, githubCommenterLogin, arguments)
    { }

    public FuzzLibrariesJob(RuntimeUtilsService parent, PullRequest pullRequest, string githubCommenterLogin, string arguments, GitHubComment comment)
        : base(parent, pullRequest, githubCommenterLogin, arguments, comment)
    { }

    protected override async Task RunJobAsyncCore(CancellationToken jobTimeout)
    {
        await JobCompletionTcs.Task;

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

        await SetFinalTrackingIssueBodyAsync(
            $"""
            {errorStackTraces}
            {(FirstErrorMessage is null && string.IsNullOrEmpty(errorStackTraces) ? "Ran the fuzzer(s) successfully." : "")}
            """);

        if (!string.IsNullOrEmpty(errorStackTraces) &&
            ShouldLinkToPROrBranch &&
            ShouldMentionJobInitiator &&
            PullRequest is not null)
        {
            string artifacts;
            lock (Artifacts)
            {
                artifacts = string.Join('\n', _errorStackTraces
                    .Select(error => Artifacts.FirstOrDefault(a => a.FileName == $"{error.Key}-input.bin"))
                    .Where(error => error.Url is not null)
                    .Select(error => $"- [{error.FileName}]({error.Url}) ({GetRoughSizeString(error.Size)})"));
            }

            await Github.Issue.Comment.Create(RepoOwner, RepoName, PullRequest.Number,
                $"""
                {errorStackTraces}

                {artifacts}
                """);

            ShouldMentionJobInitiator = false;
        }

        if (string.IsNullOrEmpty(errorStackTraces) &&
            string.IsNullOrEmpty(FirstErrorMessage) &&
            GitHubComment is not null)
        {
            await GitHubComment.AddReactionAsync(Octokit.ReactionType.Plus1);
        }
    }

    protected override async Task<Stream> InterceptArtifactAsync(string fileName, Stream contentStream, CancellationToken cancellationToken)
    {
        const string StackNameSuffix = "-stack.txt";
        const string InputsNameSuffix = "-inputs.zip";

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
                    string message = $"Failed to update inputs blob: {ex}";
                    Log(message);
                    await Logger.DebugAsync(message);
                }
            }

            return replacement;
        }

        return null;
    }
}
