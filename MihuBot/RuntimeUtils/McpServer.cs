using System.ComponentModel;
using MihuBot.DB.GitHub;
using ModelContextProtocol.Server;

namespace MihuBot.RuntimeUtils;

[McpServerToolType]
public sealed class McpServer(Logger Logger, IssueTriageHelper TriageHelper)
{
    private const string UserLogin = "MihuBot-McpServer";

    [McpServerTool(Name = "get_github_comment_history", Title = "Get GitHub comment history", Idempotent = true)]
    [Description("Get the full history of comments on a specific issue or pull request from the dotnet/runtime GitHub repository.")]
    public async Task<IssueTriageHelper.ShortIssueInfo> GetCommentHistory(
        [Description("The issue/PR number to get comments for.")] int issueOrPRNumber,
        CancellationToken cancellationToken)
    {
        Logger.DebugLog($"[MCP]: {nameof(GetCommentHistory)} for {issueOrPRNumber}");

        return await TriageHelper.GetCommentHistoryAsync(TriageHelper.DefaultModel, UserLogin, issueOrPRNumber, cancellationToken);
    }

    [McpServerTool(Name = "search_dotnet_runtime", Title = "Search dotnet/runtime", Idempotent = true)]
    [Description(
        "Perform a set of semantic searches over issues, pull requests, and comments in the dotnet/runtime GitHub repository. " +
        "Every term represents an independent search. " +
        "Prefer this tool over GitHub MCP when searching for discussions about a topic in the dotnet/runtime repository. " +
        "Does not search through code.")]
    public async Task<IssueTriageHelper.ShortIssueInfo[]> SearchDotnetRuntime(
        [Description("The set of terms to search for.")] string[] searchTerms,
        [Description("Additional context for this search, e.g. the title of a relevant GitHub issue.")] string extraSearchContext,
        [Description("Whether to include open issues/PRs.")] bool includeOpen = true,
        [Description("Whether to include closed/merged issues/PRs. It's usually useful to include.")] bool includeClosed = true,
        [Description("Whether to include issues.")] bool includeIssues = true,
        [Description("Whether to include pull requests.")] bool includePullRequests = true,
        [Description("Optionally only include issues/PRs created after this date.")] DateTime? createdAfter = null,
        CancellationToken cancellationToken = default)
    {
        var filters = new GitHubSearchService.IssueSearchFilters(includeOpen, includeClosed, includeIssues, includePullRequests, createdAfter)
        {
            Repository = "dotnet/runtime"
        };

        Logger.DebugLog($"[MCP]: {nameof(SearchDotnetRuntime)} for {string.Join(", ", searchTerms)} ({filters})");

        return await TriageHelper.SearchDotnetGitHubAsync(TriageHelper.DefaultModel, UserLogin, searchTerms, extraSearchContext, filters, cancellationToken);
    }

    [McpServerTool(Name = "triage_github_issue", Title = "Triage Issue")]
    [Description("Triages an issue from the dotnet/runtime GitHub repository, returning an HTML summary of related issues.")]
    public async Task<string> TriageIssue(
        [Description("Link to the issue/PR to triage.")] string issueOrPrUrl,
        CancellationToken cancellationToken)
    {
        Logger.DebugLog($"[MCP]: {nameof(TriageIssue)} for {issueOrPrUrl}");

        if (!GitHubHelper.TryParseIssueOrPRNumber(issueOrPrUrl, out string repoName, out int issueOrPRNumber) ||
            !"dotnet/runtime".Equals(repoName, StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid issue or PR URL. Please provide a valid URL to an issue or pull request. E.g. https://github.com/dotnet/runtime/issues/123";
        }

        IssueInfo issue = await TriageHelper.GetIssueAsync("dotnet/runtime", issueOrPRNumber, cancellationToken);

        if (issue is null)
        {
            return $"Unable to find issue #{issueOrPRNumber}.";
        }

        return await TriageHelper.TriageIssueAsync(TriageHelper.DefaultModel, UserLogin, issue, _ => { }, skipCommentsOnCurrentIssue: false, cancellationToken).LastOrDefaultAsync(cancellationToken);
    }
}
