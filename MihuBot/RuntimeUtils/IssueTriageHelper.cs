using System.ComponentModel;
using System.Runtime.CompilerServices;
using Markdig.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;
using Octokit;
using IssueSearchResult = MihuBot.RuntimeUtils.GitHubSearchService.IssueSearchResult;

namespace MihuBot.RuntimeUtils;

public sealed class IssueTriageHelper(Logger Logger, IDbContextFactory<GitHubDbContext> GitHubDb, GitHubSearchService Search, OpenAIService OpenAI)
{
    public sealed record ModelInfo(string Name, int ContextSize, bool SupportsTemperature);

    public ModelInfo[] AvailableModels { get; } = [new("gpt-4.1", 1_000_000, true), new("o4-mini", 200_000, false)];
    public ModelInfo DefaultModel => AvailableModels[0];

    public IAsyncEnumerable<string> TriageIssueAsync(IssueInfo issue, ModelInfo model, string gitHubUserLogin, Action<string> onToolLog, CancellationToken cancellationToken)
    {
        var context = new Context
        {
            Parent = this,
            Logger = Logger,
            GitHubDb = GitHubDb,
            Search = Search,
            OpenAI = OpenAI,
            Issue = issue,
            Model = model,
            OnToolLog = onToolLog,
        };

        return context.TriageAsync(cancellationToken);
    }

    public async Task<IssueInfo> GetIssueAsync(int issueNumber, CancellationToken cancellationToken)
    {
        return await GetIssueAsync(issues => issues.Where(i => i.Number == issueNumber), cancellationToken);
    }

    public async Task<IssueInfo> GetIssueAsync(Func<IQueryable<IssueInfo>, IQueryable<IssueInfo>> query, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = GitHubDb.CreateDbContext();

        IQueryable<IssueInfo> issues = db.Issues
            .AsNoTracking();

        issues = query(issues);

        return await issues
            .Include(i => i.User)
            .Include(i => i.PullRequest)
            .Include(i => i.Labels)
            .Include(i => i.Repository)
                .ThenInclude(r => r.Owner)
            .Include(i => i.Comments)
                .ThenInclude(c => c.User)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed class Context
    {
        private const string SystemPrompt =
            """
            You are an assistant helping the .NET team triage new GitHub issues in the https://github.com/dotnet/runtime repository.
            You are provided with information about a new issue and you need to find related issues or comments among the existing GitHub data.

            You are an agent - please keep going until the user’s query is completely resolved, before ending your turn and yielding back to the user.
            Assume that the user will NOT be able to reply and ask futher, more refined questions. You MUST iterate and keep going until you have all the information.
            Only terminate your turn when you are sure that the problem is solved.
            Your thinking should be thorough and so it's fine if it's very long. You can think step by step before and after each action you decide to take.

            Use tools to find issues and comments that may be relevant. It is up to you to determine whether the results are relevant.
            Be thorough with your searches and always consider the full discussion on related issues. You should perform as many searches as needed{{MAX_SEARCH_COUNT}}.
            Tools use semantic searching to find issues and comments that may be relevant to the issue you are triaging. You can search for multiple terms at the same time.

            When evaulating an issue, always use tools to ask for the FULL history of comments on that issue.
            Even if you've seen previous comments from an issue, you haven't seen all of them unless you've called the full history for that specific issue.
            Use your tools to gather the relevant information from comments: do NOT guess or make up conclusions for a given issue.
            Pay close attention to comments when summarizing the issue as questions may have already been answered.

            Reply with a list of related issues and include a short summary of the discussions/conclusions for each one.
            Assume that the user is familiar with the repository and its processes, but not necessarily with the specific issue or discussion.

            Reply in GitHub markdown format, not wrapped in any HTML blocks.
            """;

        private static readonly HashSet<string> s_networkingTeam = new(
            ["MihaZupan", "CarnaViire", "karelz", "antonfirsov", "ManickaP", "wfurt", "rzikm", "liveans", "rokonec"],
            StringComparer.OrdinalIgnoreCase);

        public IssueTriageHelper Parent { get; set; }
        public Logger Logger { get; set; }
        public IDbContextFactory<GitHubDbContext> GitHubDb { get; set; }
        public GitHubSearchService Search { get; set; }
        public OpenAIService OpenAI { get; set; }
        public IssueInfo Issue { get; set; }
        public ModelInfo Model { get; set; }
        public string GitHubUserLogin { get; set; }
        public bool UsingLargeContextWindow => Model.ContextSize >= 500_000;
        public Action<string> OnToolLog { get; set; }

        public async IAsyncEnumerable<string> TriageAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Logger.DebugLog($"Starting triage for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            OnToolLog($"{Issue.Title} by {Issue.User.Login}");

            var options = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(SearchDotnetRuntimeAsync),
                    AIFunctionFactory.Create(GetCommentHistoryAsync),
                ],
                ToolMode = ChatToolMode.RequireAny
            };

            if (Model.SupportsTemperature)
            {
                options.Temperature = 0;
            }

            string systemPrompt = SystemPrompt
                .Replace("{{MAX_SEARCH_COUNT}}", UsingLargeContextWindow ? "" : " (max of 5)", StringComparison.Ordinal);

            List<ChatMessage> messages = [new ChatMessage(ChatRole.System, systemPrompt)];

            messages.Add(new ChatMessage(ChatRole.User,
                $"""
                Please help me triage issue #{Issue.Number} from {Issue.User.Login} titled '{Issue.Title}'.
                Existing comments: {Issue.Comments.Count(c => !c.User.Login.EndsWith("[bot]", StringComparison.Ordinal))}

                Here is the issue:
                {Issue.Body}
                """));

            using IChatClient chatClient = OpenAI.GetChat(Model.Name, secondary: true);
            using var toolClient = new FunctionInvokingChatClient(chatClient);
            toolClient.AllowConcurrentInvocation = true;
            toolClient.MaximumIterationsPerRequest = 20;

            string markdownResponse = "";

            await foreach (ChatResponseUpdate update in toolClient.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                string updateText = update.Text;

                if (string.IsNullOrEmpty(updateText))
                {
                    continue;
                }

                markdownResponse += updateText;

                yield return ConvertMarkdownToHtml(markdownResponse, partial: true);
            }

            Logger.DebugLog($"Triage: Finished triaging issue #{Issue.Number} with model {Model.Name}:\n{markdownResponse}");

            yield return ConvertMarkdownToHtml(markdownResponse, partial: false);
        }

        private static string ConvertMarkdownToHtml(string markdown, bool partial)
        {
            MarkdownDocument document = MarkdownHelper.ParseAdvanced(markdown);

            if (partial)
            {
                MarkdownHelper.FixUpPartialDocument(document);
            }

            MarkdownHelper.ReplaceGitHubIssueReferencesWithLinks(document, "dotnet/runtime");

            MarkdownHelper.ReplaceGitHubUserMentionsWithLinks(document);

            return document.ToHtmlAdvanced();
        }

        [Description("Get the full history of comments on a specific issue or pull request from the dotnet/runtime GitHub repository.")]
        private async Task<string[]> GetCommentHistoryAsync(
            [Description("The issue/PR number to get comments for.")] int issueOrPRNumber,
            CancellationToken cancellationToken)
        {
            IssueInfo issue = await Parent.GetIssueAsync(issueOrPRNumber, cancellationToken);

            if (issue is null)
            {
                OnToolLog($"[Tool] Issue #{issueOrPRNumber} not found.");
                return [$"Issue #{issueOrPRNumber} does not appear to exist."];
            }

            CommentInfo[] comments = issue.Comments
                .OrderByDescending(c => c.CreatedAt)
                .Where(c => !SemanticMarkdownChunker.IsUnlikelyToBeUseful(issue, c))
                .ToArray();

            int maxComments = UsingLargeContextWindow ? 200 : 50;

            if (comments.Length > maxComments)
            {
                comments = [.. comments.AsSpan(0, maxComments / 2), .. comments.AsSpan(comments.Length - (maxComments / 2))];
            }

            OnToolLog($"[Tool] Obtained {comments.Length} comments for issue #{issue.Number}: {issue.Title}");

            string originalIssue = CreateCommentText(issue.CreatedAt, issue.User, issue.Body, issue, comment: null);

            return [originalIssue, .. comments.Select(c => CreateCommentText(c.CreatedAt, c.User, c.Body, issue, c))];
        }

        [Description(
            "Perform a set of semantic searches over issues and comments in the dotnet/runtime GitHub repository." +
            " Every term represents an independent search.")]
        private async Task<string[]> SearchDotnetRuntimeAsync(
            [Description("The set of terms to search for.")] string[] searchTerms,
            CancellationToken cancellationToken)
        {
            OnToolLog($"[Tool] Searching for {string.Join(", ", searchTerms)}");
            Stopwatch stopwatch = Stopwatch.StartNew();

            List<(IssueSearchResult[] Results, double Score)> searchResults = new();

            int maxTotalResults = UsingLargeContextWindow
                ? Math.Clamp(searchTerms.Length * 50, 75, 200)
                : Math.Clamp(searchTerms.Length * 25, 25, 50);
            int maxResultsPerTerm = searchTerms.Length > 1 ? (UsingLargeContextWindow ? 50 : 25) : maxTotalResults;

            await Parallel.ForEachAsync(searchTerms, async (term, _) =>
            {
                IssueSearchResult[] localResults = (await Search.SearchIssuesAndCommentsAsync(term, maxResultsPerTerm, cancellationToken))
                    .Where(r => r.Score > 0.25)
                    .Where(r => r.Comment is null || !SemanticMarkdownChunker.IsUnlikelyToBeUseful(r.Issue, r.Comment))
                    .ToArray();

                (IssueSearchResult[] Results, double Score)[] resultsByIssueId = GitHubSearchService.GroupResultsByIssue(localResults);

                try
                {
                    resultsByIssueId = await Search.FilterOutUnrelatedResults(term, $"on issue titled '{Issue.Title}'", preferSpeed: false, resultsByIssueId, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.DebugLog($"Triage: Error filtering unrelated results: {ex.Message}");
                }

                lock (searchResults)
                {
                    searchResults.AddRange(resultsByIssueId);
                }
            });

            var combinedResults = searchResults
                .GroupBy(r => r.Results[0].Issue.Id)
                .Select(g => (
                    Score: GitHubSearchService.EstimateCombinedScores(g.Select(r => r.Score).ToArray()),
                    Results: g.SelectMany(r => r.Results).DistinctBy(e => e.Comment?.Id).OrderBy(e => e.Comment?.CreatedAt ?? e.Issue.CreatedAt).ToArray()))
                .OrderByDescending(g => g.Score)
                .ToArray();

            List<string> results = [];

            int issueReferences = 0;
            int searchIssues = 0;
            int searchComments = 0;

            foreach (string term in searchTerms)
            {
                if (GitHubHelper.TryParseIssueOrPRNumber(term, dotnetRuntimeOnly: true, out int issueNumber) &&
                    await Parent.GetIssueAsync(issueNumber, cancellationToken) is { } singleIssue)
                {
                    issueReferences++;

                    results.Add(
                        $"""
                        {GetIssueHeader(score: 1, singleIssue)}
                        {CreateCommentText(singleIssue.CreatedAt, singleIssue.User, singleIssue.Body, singleIssue, comment: null)}
                        """);
                }
            }

            string[] gitHubIssueResults = combinedResults
                .Select(r =>
                {
                    IssueSearchResult[] results = r.Results;
                    bool hasIssue = results.Any(r => r.Comment is null);

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine(GetIssueHeader(r.Score, results[0].Issue));

                    if (results.FirstOrDefault(r => r.Comment is null)?.Issue is { } issue)
                    {
                        searchIssues++;
                        sb.AppendLine();
                        sb.AppendLine(CreateCommentText(issue.CreatedAt, issue.User, issue.Body, issue, comment: null));
                    }

                    foreach (IssueSearchResult result in results)
                    {
                        if (result.Comment is { } comment)
                        {
                            searchComments++;
                            sb.AppendLine();
                            sb.AppendLine(CreateCommentText(comment.CreatedAt, comment.User, comment.Body, result.Issue, comment));
                        }
                    }

                    return sb.ToString();
                })
                .Take(maxTotalResults)
                .ToArray();

            results.AddRange(gitHubIssueResults);

            OnToolLog($"[Tool] Found {searchIssues} issues, {searchComments} comments, {results.Count} returned results ({(int)stopwatch.ElapsedMilliseconds} ms)");

            return [.. results];

            static string GetIssueHeader(double score, IssueInfo issue)
            {
                string type = "Issue";
                string suffix = "";

                if (issue.PullRequest is { } pullRequest)
                {
                    type = "Pull request";

                    if (!pullRequest.MergedAt.HasValue && issue.State == ItemState.Closed)
                    {
                        suffix = ", never got merged";
                    }
                }

                return
                    $"""
                    Simmilarity score: {score:F2}
                    {type} #{issue.Number} - '{issue.Title}' by {issue.User.Login} ({issue.Comments.Count} total comments{suffix}):
                    """;
            }
        }

        private string CreateCommentText(DateTimeOffset createdAt, UserInfo author, string text, IssueInfo issue, CommentInfo comment)
        {
            string authorSuffix = s_networkingTeam.Contains(author.Login)
                ? " (member of the .NET networking team)"
                : string.Empty;

            return
                $"""
                {author.Login}{authorSuffix} {(comment is null ? "wrote" : "commented")} on {createdAt:yyyy-MM-dd}:
                {SemanticMarkdownChunker.TrimTextToTokens(Search.Tokenizer, text, 2_000)}
                {FormatReactions(issue, comment)}
                """.Trim(' ', '\t', '\n', '\r');

            static string FormatReactions(IssueInfo issue, CommentInfo comment)
            {
                int positive =
                    (comment?.Plus1 ?? issue.Plus1) +
                    (comment?.Heart ?? issue.Heart) +
                    (comment?.Hooray ?? issue.Hooray) +
                    (comment?.Rocket ?? issue.Rocket);

                int negative =
                    (comment?.Minus1 ?? issue.Minus1) +
                    (comment?.Confused ?? issue.Confused);

                if (positive > 0 || negative > 0)
                {
                    return $"Community reacted with {positive} upvotes, {negative} downvotes";
                }

                return string.Empty;
            }
        }
    }
}
