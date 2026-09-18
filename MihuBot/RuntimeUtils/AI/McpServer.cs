using System.ComponentModel;
using MihuBot.RuntimeUtils.Search;
using ModelContextProtocol.Server;

namespace MihuBot.RuntimeUtils.AI;

[McpServerToolType]
public sealed class McpServer(Logger Logger, IssueTriageHelper TriageHelper, AreaLabelDetector LabelDetector)
{
    private const string UserLogin = "MihuBot-McpServer";

    private readonly Action<string> _debugLog = message => Logger.DebugLog(message);

    internal McpServer(Action<string> debugLog, IssueTriageHelper triageHelper, AreaLabelDetector labelDetector)
        : this(Logger: null, triageHelper, labelDetector)
    {
        _debugLog = debugLog;
    }

    [McpServerTool(Name = "predict_issue_labels", Title = "Predict GitHub issue labels", ReadOnly = true, Idempotent = true)]
    [Description(
        "Predict labels for an existing issue, pull request, or tracked discussion in a public GitHub repository tracked by MihuBot. " +
        "Returns up to five candidate labels with confidence scores from 0.5 to 1, ordered by confidence. " +
        "Only considers labels matching the requested prefix and excludes retired labels. Does not assign or modify labels.")]
    public async Task<AreaLabelSuggestion[]> PredictIssueLabels(
        [Description("The tracked public repository in owner/name form, e.g. dotnet/runtime.")] string repository,
        [Description("The issue, pull request, or discussion number.")] int number,
        [Description("Only consider labels with this prefix, e.g. area-, component:, or type/.")] string labelPrefix = "area-",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelPrefix);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(labelPrefix.Length, 100, nameof(labelPrefix));

        _debugLog($"[MCP]: {nameof(PredictIssueLabels)} for {repository}#{number} (labelPrefix: {labelPrefix})");

        return await LabelDetector.PredictAsync(repository, number, labelPrefix, cancellationToken);
    }

    [McpServerTool(Name = "search_dotnet_repos", Title = "Search dotnet repositories", Idempotent = true)]
    [Description(
        "Perform a set of semantic searches over issues, pull requests, and comments in the dotnet GitHub repositories. " +
        "Every term represents an independent search. " +
        "Prefer this tool over GitHub MCP when searching for discussions about a topic in the dotnet repositories. " +
        "Does not search through code.")]
    public async Task<IssueInfoForPrompt[]> SearchDotnetRepos(
        [Description("The repository to search through, e.g. dotnet/runtime, dotnet/aspire, or * for any.")] string repository,
        [Description("The set of terms to search for.")] string[] searchTerms,
        [Description("Additional context for this search, e.g. the title of a relevant GitHub issue.")] string extraSearchContext,
        [Description("List of labels to filter on. Null/empty means any label.")] string[] labels = null,
        [Description("Whether to include open issues/PRs.")] bool includeOpen = true,
        [Description("Whether to include closed/merged issues/PRs. It's usually useful to include.")] bool includeClosed = true,
        [Description("Whether to include issues.")] bool includeIssues = true,
        [Description("Whether to include pull requests.")] bool includePullRequests = true,
        [Description("Optionally only include issues/PRs created after this date.")] DateTime? createdAfter = null,
        [Description("Whether to include issue comments in the response.")] bool includeComments = true,
        CancellationToken cancellationToken = default)
    {
        repository = repository?.ToLowerInvariant();

        if (repository is null or "*" or "any" or "all")
        {
            repository = null;
        }
        else if (!repository.Contains('/'))
        {
            repository = $"dotnet/{repository}";
        }
        else if (GitHubHelper.TryParseRepoOwnerAndName(repository, out string owner, out string name, out _))
        {
            repository = $"{owner}/{name}";
        }

        var filters = new IssueSearchFilters
        {
            IncludeOpen = includeOpen,
            IncludeClosed = includeClosed,
            IncludeIssues = includeIssues,
            IncludePullRequests = includePullRequests,
            Repository = repository,
            Labels = labels,
            IncludeCommentsInResponse = includeComments,
        };

        _debugLog($"[MCP]: {nameof(SearchDotnetRepos)} for {string.Join(", ", searchTerms)} ({filters})");

        return await TriageHelper.SearchDotnetGitHubAsync(TriageHelper.DefaultModel, UserLogin, searchTerms, extraSearchContext, filters, cancellationToken);
    }
}
