using System.ComponentModel;
using MihuBot.DB.GitHub;
using ModelContextProtocol.Server;

namespace MihuBot.RuntimeUtils;

[McpServerToolType]
public sealed class McpServer(Logger Logger, IssueTriageHelper TriageHelper)
{
    private const string UserLogin = "MihuBot-McpServer";

    [McpServerTool]
    [Description("Get the full history of comments on a specific issue or pull request from the dotnet/runtime GitHub repository.")]
    public async Task<string[]> GetCommentHistory(
        [Description("The issue/PR number to get comments for.")] int issueOrPRNumber,
        CancellationToken cancellationToken)
    {
        Logger.DebugLog($"[MCP]: {nameof(GetCommentHistory)} for {issueOrPRNumber}");

        return await TriageHelper.GetCommentHistoryAsync(TriageHelper.DefaultModel, UserLogin, issueOrPRNumber, cancellationToken);
    }

    [McpServerTool]
    [Description("Perform a set of semantic searches over issues and comments in the dotnet/runtime GitHub repository. Every term represents an independent search.")]
    public async Task<string[]> SearchDotnetRuntime(
        [Description("The set of terms to search for.")] string[] searchTerms,
        [Description("Additional context for this search, e.g. the title of a relevant GitHub issue.")] string extraSearchContext,
        CancellationToken cancellationToken)
    {
        return await TriageHelper.SearchDotnetRuntimeAsync(TriageHelper.DefaultModel, UserLogin, searchTerms, extraSearchContext, cancellationToken);
    }

    [McpServerTool]
    [Description("Triages an issue from the dotnet/runtime GitHub repository, returning an HTML summary of related issues.")]
    public async Task<string> TriageIssue(
        [Description("The issue/PR number to triage.")] int issueOrPRNumber,
        CancellationToken cancellationToken)
    {
        IssueInfo issue = await TriageHelper.GetIssueAsync(issueOrPRNumber, cancellationToken);

        if (issue is null)
        {
            return $"Unable to find issue #{issueOrPRNumber}.";
        }

        return await TriageHelper.TriageIssueAsync(TriageHelper.DefaultModel, UserLogin, issue, _ => { }, cancellationToken).LastOrDefaultAsync(cancellationToken);
    }
}
