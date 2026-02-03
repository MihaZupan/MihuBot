using System.ComponentModel;
using MihuBot.RuntimeUtils.Search;
using ModelContextProtocol.Server;

namespace MihuBot.RuntimeUtils.AI;

[McpServerToolType]
public sealed class McpServer(Logger Logger, IssueTriageHelper TriageHelper)
{
    private const string UserLogin = "MihuBot-McpServer";

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
        [Description("Whether to include open issues/PRs.")] bool includeOpen = true,
        [Description("Whether to include closed/merged issues/PRs. It's usually useful to include.")] bool includeClosed = true,
        [Description("Whether to include issues.")] bool includeIssues = true,
        [Description("Whether to include pull requests.")] bool includePullRequests = true,
        [Description("Optionally only include issues/PRs created after this date.")] DateTime? createdAfter = null,
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

        var filters = new IssueSearchFilters
        {
            IncludeOpen = includeOpen,
            IncludeClosed = includeClosed,
            IncludeIssues = includeIssues,
            IncludePullRequests = includePullRequests,
            Repository = repository
        };

        Logger.DebugLog($"[MCP]: {nameof(SearchDotnetRepos)} for {string.Join(", ", searchTerms)} ({filters})");

        return await TriageHelper.SearchDotnetGitHubAsync(TriageHelper.DefaultModel, UserLogin, searchTerms, extraSearchContext, filters, cancellationToken);
    }
}
