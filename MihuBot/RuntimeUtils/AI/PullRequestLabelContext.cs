using System.Runtime.Serialization;
using System.Text.Json;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;
using static MihuBot.RuntimeUtils.DataIngestion.GitHub.GitHubGraphQL;

namespace MihuBot.RuntimeUtils.AI;

public sealed class PullRequestLabelContext(GitHubClient github, GithubGraphQLClient graphQL, Logger logger)
{
    internal const int MaxFiles = 300;
    internal const int MaxHistoryPaths = 12;
    internal const int MaxPatchCharacters = 24_000;
    internal const int MaxPatchCharactersPerFile = 3000;
    internal const int MaxSessionPromptCharacters = 6000;
    internal const int MaxCopilotTasks = 3;
    internal const int MaxCopilotSessions = 10;
    private static readonly TimeSpan CopilotRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions CopilotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Action<string> _debugLog = message => logger.DebugLog(message);

    internal PullRequestLabelContext(GitHubClient github, GithubGraphQLClient graphQL, Action<string> debugLog)
        : this(github, graphQL, (Logger)null)
    {
        _debugLog = debugLog;
    }

    internal async Task<Context> GetAsync(IssueInfo issue, string[] labels, CancellationToken cancellationToken)
    {
        string[] repository = issue.Repository.FullName.Split('/');
        var (data, metadataCalls, metadataCost) = await graphQL.GetPullRequestLabelInfoAsync(repository[0], repository[1], issue.Number, cancellationToken);
        var pr = data?.PullRequest
            ?? throw new NotFoundException("Pull request not found.", HttpStatusCode.NotFound);

        if (data.IsPrivate)
        {
            throw new NotFoundException("Repository is not public.", HttpStatusCode.NotFound);
        }

        var references = GetDescriptionReferences(pr.Body, issue.Repository.FullName, issue.Number,
            pr.ClosingIssuesReferences.Nodes.Select(i => i.Url));
        var fileEvidenceTask = GetFileEvidenceAsync();
        var referencesTask = graphQL.GetReferencedLabelItemsAsync(
            references, _debugLog, cancellationToken);
        var sessionsTask = GetCopilotPromptsAsync(issue.Repository.FullName, pr, cancellationToken);
        await Task.WhenAll(fileEvidenceTask, referencesTask, sessionsTask);

        var (changes, paths, examples, historyCalls, historyCost) = await fileEvidenceTask;
        var (mentioned, referenceCalls, referenceCost) = await referencesTask;
        var (prompts, promptStatus) = await sessionsTask;
        _debugLog($"PR label evidence for <{pr.Url}>: {metadataCalls + historyCalls + referenceCalls} GraphQL API calls, cost {metadataCost + historyCost + referenceCost}.");

        return new Context(pr, changes, changes.Length < pr.ChangedFiles, paths, examples,
            [.. pr.ClosingIssuesReferences.Nodes
                .Where(i => !i.Repository.IsPrivate)
                .Select(CreateRelatedItem)],
            [.. mentioned.Where(i => !i.Url.Equals(pr.Url, StringComparison.OrdinalIgnoreCase) &&
                !pr.ClosingIssuesReferences.Nodes.Any(c => c.Url.Equals(i.Url, StringComparison.OrdinalIgnoreCase))).Select(CreateRelatedItem)],
            prompts, promptStatus);

        async Task<(FileEvidence[] Files, HistoryPath[] Paths, HistoricalPullRequest[] Examples, int Calls, int Cost)> GetFileEvidenceAsync()
        {
            var files = await github.PullRequest.Files(issue.RepositoryId, issue.Number,
                new ApiOptions { PageSize = 100, PageCount = MaxFiles / 100 })
                .WaitAsyncAndSupressNotObserved(cancellationToken);
            var changes = CreateFileEvidence(files);
            var paths = SelectHistoryPaths(changes);
            var (examples, calls, cost) = await GetHistoryAsync(repository, pr.BaseRefOid, paths, pr.Url, labels, cancellationToken);
            return (changes, paths, examples, calls, cost);
        }

        RelatedItem CreateRelatedItem(LinkedItemLabelInfoModel item) => new RelatedItem(
            item.Url, Trim(item.Title, AreaLabelDetector.MaxRelatedTitleCharacters), Trim(item.Body, AreaLabelDetector.MaxContextBodyCharacters),
            item.Author?.Login, GetLabels(item.Labels, labels, item.Repository.NameWithOwner.Equals(issue.Repository.FullName, StringComparison.OrdinalIgnoreCase)));
    }

    internal static (string Repository, int Number)[] GetDescriptionReferences(
        string body, string repository, int number, IEnumerable<string> closingIssueUrls)
    {
        HashSet<(string Repository, int Number)> seen = [(repository.ToLowerInvariant(), number)];

        foreach (string url in closingIssueUrls)
        {
            if (GitHubHelper.TryParseIssueOrPRNumber(url, out string closingRepository, out int closingNumber) && closingRepository is not null)
            {
                seen.Add((closingRepository.ToLowerInvariant(), closingNumber));
            }
        }

        return [.. GitHubHelper.ExtractIssueOrPullRequestReferences(body, repository)
            .Select(r => (Repository: r.Repository.ToLowerInvariant(), r.Number))
            .Where(seen.Add)
            .Take(ReferencedLabelItemsBatchSize)];
    }

    internal static FileEvidence[] CreateFileEvidence(IEnumerable<PullRequestFile> files)
    {
        var ordered = files.OrderByDescending(f => f.Changes).ThenBy(f => f.FileName, StringComparer.Ordinal).Take(MaxFiles).ToArray();
        int patchLimit = Math.Min(MaxPatchCharactersPerFile, MaxPatchCharacters / Math.Max(1, ordered.Count(f => !string.IsNullOrEmpty(f.Patch))));

        return [.. ordered.Select(f => new FileEvidence(f.FileName, f.PreviousFileName, f.Status, f.Additions, f.Deletions,
            Trim(f.Patch, patchLimit), string.IsNullOrEmpty(f.Patch) || f.Patch.Length > patchLimit))];
    }

    internal static HistoryPath[] SelectHistoryPaths(FileEvidence[] files)
    {
        // Sample across directories rather than letting one generated-code subtree consume the history budget.
        return [.. files
            .Select(f => new HistoryPath(f.PreviousPath ?? f.Path, false))
            .DistinctBy(p => p.Path, StringComparer.Ordinal)
            .GroupBy(p => DirectoryOf(p.Path), StringComparer.Ordinal)
            .SelectMany(g => g.Select((p, index) => (Path: p, Index: index)))
            .OrderBy(p => p.Index)
            .Take(MaxHistoryPaths)
            .Select(p => p.Path)];
    }

    private async Task<(HistoricalPullRequest[] PullRequests, int Calls, int Cost)> GetHistoryAsync(
        string[] repository, string baseOid, HistoryPath[] paths, string targetUrl, string[] labels, CancellationToken cancellationToken)
    {
        if (paths.Length == 0)
        {
            return ([], 0, 0);
        }

        int calls = 0;
        int cost = 0;
        var matches = await ReadHistoryAsync(paths);
        string fullName = string.Join('/', repository);
        HistoryPath[] fallbackPaths = [.. paths
            .Where(p => !matches.Any(m => m.Path == p && IsEligibleHistory(m.PullRequest, targetUrl, fullName, labels)))
            .Select(p => DirectoryOf(p.Path))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(p => new HistoryPath(p, true))];

        if (fallbackPaths.Length > 0)
        {
            matches.AddRange(await ReadHistoryAsync(fallbackPaths));
        }

        return (RankHistory(matches, targetUrl, fullName, labels), calls, cost);

        async Task<List<HistoryMatch>> ReadHistoryAsync(HistoryPath[] historyPaths)
        {
            var pathsByName = historyPaths.ToDictionary(p => p.Path, StringComparer.Ordinal);
            var (matches, queryCalls, queryCost) = await graphQL.GetPullRequestFileHistoryAsync(
                repository[0], repository[1], baseOid, [.. historyPaths.Select(p => p.Path)], cancellationToken);
            calls += queryCalls;
            cost += queryCost;
            return [.. matches.Select(m => new HistoryMatch(pathsByName[m.Path], m.PullRequest))];
        }
    }

    internal static HistoricalPullRequest[] RankHistory(IEnumerable<HistoryMatch> matches, string targetUrl, string repository, string[] labels) =>
        [.. matches
            .Where(m => IsEligibleHistory(m.PullRequest, targetUrl, repository, labels))
            .GroupBy(m => m.PullRequest.Url, StringComparer.Ordinal)
            .OrderByDescending(g => g.Where(m => !m.Path.Directory).Select(m => m.Path.Path).Distinct(StringComparer.Ordinal).Count())
            .ThenByDescending(g => g.Select(m => m.Path).Distinct().Count())
            .ThenByDescending(g => g.First().PullRequest.MergedAt)
            .Take(10)
            .Select(g =>
            {
                var pr = g.First().PullRequest;
                return new HistoricalPullRequest(pr.Url, Trim(pr.Title, AreaLabelDetector.MaxRelatedTitleCharacters),
                    Trim(pr.Body, AreaLabelDetector.MaxContextBodyCharacters), pr.Author?.Login,
                    GetLabels(pr.Labels, labels, true), [.. g.Select(m => m.Path).Distinct()]);
            })];

    private static bool IsEligibleHistory(PullRequestHistoryModel pr, string targetUrl, string repository, string[] labels) =>
        pr.Url != targetUrl && pr.MergedAt is not null && !pr.Repository.IsPrivate &&
        pr.Repository.NameWithOwner.Equals(repository, StringComparison.OrdinalIgnoreCase) &&
        pr.Labels.Nodes.Any(l => labels.Contains(l.Name, StringComparer.OrdinalIgnoreCase));

    private async Task<(SessionPrompt[] Prompts, string Status)> GetCopilotPromptsAsync(
        string repository, PullRequestLabelInfoModel pr, CancellationToken cancellationToken)
    {
        var (tasks, listError) = await GetCopilotDataAsync<CopilotTaskCollection>($"agents/repos/{repository}/tasks",
            new Dictionary<string, string> { ["per_page"] = "100", ["sort"] = "updated_at", ["direction"] = "desc" }, cancellationToken);

        if (listError is not null)
        {
            return ([], $"Copilot session prompts unavailable: {listError}");
        }

        // REST-started tasks may target an existing human-authored PR. Use trusted artifacts, not the author or task links in PR text.
        string[] taskIds = FindTaskIds(tasks, pr, repository);

        if (taskIds.Length == 0)
        {
            return ([], "No matching task in the 100 most recently updated accessible, non-archived tasks.");
        }

        var results = await Task.WhenAll(taskIds.Select(taskId =>
            GetCopilotDataAsync<CopilotTask>($"agents/repos/{repository}/tasks/{Uri.EscapeDataString(taskId)}", null, cancellationToken)));
        SessionPrompt[] prompts = [.. results
            .Where(r => r.Error is null)
            .SelectMany(r => ReadSessionPrompts(r.Data))
            .OrderBy(p => p.CreatedAt)
            .TakeLast(MaxCopilotSessions)];
        string[] errors = [.. results.Where(r => r.Error is not null).Select(r => r.Error).Distinct(StringComparer.Ordinal)];

        if (errors.Length > 0)
        {
            int failed = results.Count(r => r.Error is not null);
            return (prompts, $"Copilot session prompts {(failed == results.Length ? "unavailable" : "partially unavailable")}; {failed}/{results.Length} task lookups failed: {string.Join(" ", errors)}");
        }

        return (prompts, prompts.Length == 0
            ? "Matching tasks found, but no session prompts are available yet."
            : $"Accessible Copilot session prompts; at most {MaxCopilotSessions} recent sessions, each truncated to {MaxSessionPromptCharacters} characters.");
    }

    private async Task<(T Data, string Error)> GetCopilotDataAsync<T>(
        string path, IDictionary<string, string> parameters, CancellationToken cancellationToken) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CopilotRequestTimeout);

        try
        {
            // Keep dates untyped in Octokit, whose DateTime parser rejects nanosecond precision.
            var response = await github.Connection.Get<object>(new Uri(path, UriKind.Relative), parameters,
                "application/vnd.github+json", timeout.Token);
            T data = response.HttpResponse.Body is string json ? DeserializeCopilotData<T>(json) : null;

            if (data is null ||
                (data is CopilotTaskCollection collection && (collection.Tasks is null || collection.Tasks.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id)))) ||
                (data is CopilotTask task && (task.Sessions is null || task.Sessions.Any(s => s is null))))
            {
                throw new InvalidDataException("GitHub returned incomplete Copilot task data.");
            }

            return (data, null);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TimeoutException or OperationCanceledException or InvalidDataException or JsonException or SerializationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string error = ex switch
            {
                ApiException api => $"GitHub returned HTTP {(int)api.StatusCode}.",
                OperationCanceledException or TimeoutException => "GitHub agent tasks request timed out.",
                InvalidDataException => "GitHub returned incomplete Copilot task data.",
                JsonException or SerializationException => "GitHub returned invalid Copilot task data.",
                _ => "GitHub agent tasks request failed.",
            };
            _debugLog($"Copilot session prompts unavailable for {path}: {error} {ex.Message}");
            return (null, error);
        }
    }

    internal static T DeserializeCopilotData<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, CopilotJsonOptions);

    internal static string[] FindTaskIds(CopilotTaskCollection response, PullRequestLabelInfoModel pr, string repository) =>
        [.. response.Tasks
            .Select(t => (Task: t, Match: MatchTask(t, pr, repository)))
            .Where(t => t.Match > 0)
            .OrderByDescending(t => t.Match)
            .Select(t => t.Task.Id)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCopilotTasks)];

    private static int MatchTask(CopilotTask task, PullRequestLabelInfoModel pr, string repository)
    {
        var artifacts = task.Artifacts ?? [];
        var pulls = artifacts.Where(a => a?.Type == "pull").ToArray();

        if (pulls.Length > 0)
        {
            return pulls.Any(a => a.Data is { } data &&
                (data.GlobalId == pr.Id || (pr.DatabaseId > 0 && data.Id == pr.DatabaseId))) ? 2 : 0;
        }

        if (pr.HeadRepository is not { IsPrivate: false } headRepository ||
            !headRepository.NameWithOwner.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(pr.HeadRefName) || string.IsNullOrEmpty(pr.BaseRefName))
        {
            return 0;
        }

        return artifacts.Any(a => a is { Type: "branch", Data: not null } &&
            NormalizeBranchRef(a.Data.HeadRef) == pr.HeadRefName && NormalizeBranchRef(a.Data.BaseRef) == pr.BaseRefName) ? 1 : 0;
    }

    private static string NormalizeBranchRef(string branch)
    {
        const string prefix = "refs/heads/";
        return branch?.StartsWith(prefix, StringComparison.Ordinal) == true ? branch[prefix.Length..] : branch;
    }

    internal static SessionPrompt[] ReadSessionPrompts(CopilotTask response) =>
        [.. response.Sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Prompt))
            .OrderBy(s => s.CreatedAt)
            .TakeLast(MaxCopilotSessions)
            .Select(s => new SessionPrompt(s.CreatedAt, Trim(s.Prompt, MaxSessionPromptCharacters)))];

    private static string DirectoryOf(string path) => path.LastIndexOf('/') is >= 0 and var index ? path[..index] : "";

    private static string Trim(string text, int limit) => (text ?? "").TruncateWithDotDotDot(limit);

    private static string[] GetLabels(NodesModel<LabelNameModel> labels, string[] candidates, bool sameRepository) =>
        [.. labels.Nodes.Select(l => l.Name)
            .Where(l => !sameRepository || candidates.Contains(l, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    internal sealed record Context(
        PullRequestLabelInfoModel PullRequest, FileEvidence[] Files, bool FilesTruncated, HistoryPath[] HistoryPaths,
        HistoricalPullRequest[] HistoricalPullRequests, RelatedItem[] ClosingIssues, RelatedItem[] MentionedItems,
        SessionPrompt[] CopilotSessionPrompts, string CopilotSessionStatus);
    internal sealed record FileEvidence(string Path, string PreviousPath, string Status, int Additions, int Deletions, string Patch, bool PatchIncomplete);
    internal sealed record HistoryPath(string Path, bool Directory);
    internal sealed record HistoryMatch(HistoryPath Path, PullRequestHistoryModel PullRequest);
    internal sealed record HistoricalPullRequest(string Url, string Title, string Body, string Author, string[] Labels, HistoryPath[] MatchingPaths);
    internal sealed record RelatedItem(string Url, string Title, string Body, string Author, string[] Labels);
    internal sealed record SessionPrompt(DateTimeOffset CreatedAt, string Prompt);

    internal sealed class CopilotTaskCollection
    {
        public CopilotTask[] Tasks { get; set; }
    }

    internal sealed class CopilotTask
    {
        public string Id { get; set; }
        public CopilotArtifact[] Artifacts { get; set; } = [];
        public CopilotSession[] Sessions { get; set; } = [];
    }

    internal sealed class CopilotArtifact
    {
        public string Type { get; set; }
        public CopilotArtifactData Data { get; set; }
    }

    internal sealed class CopilotArtifactData
    {
        public long Id { get; set; }
        public string GlobalId { get; set; }
        public string HeadRef { get; set; }
        public string BaseRef { get; set; }
    }

    internal sealed class CopilotSession
    {
        public DateTimeOffset CreatedAt { get; set; }
        public string Prompt { get; set; }
    }
}
