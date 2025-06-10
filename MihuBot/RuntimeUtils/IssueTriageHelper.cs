using System.ComponentModel;
using System.Runtime.CompilerServices;
using Markdig.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;
using Octokit;
using IssueSearchResult = MihuBot.RuntimeUtils.GitHubSearchService.IssueSearchResult;
using IssueSearchFilters = MihuBot.RuntimeUtils.GitHubSearchService.IssueSearchFilters;
using MihuBot.DB;

namespace MihuBot.RuntimeUtils;

public sealed class IssueTriageHelper(Logger Logger, IDbContextFactory<GitHubDbContext> GitHubDb, GitHubSearchService Search, OpenAIService OpenAI)
{
    public sealed record ModelInfo(string Name, int ContextSize, bool SupportsTemperature);

    public sealed record ShortCommentInfo(string CreatedAt, string Author, string Body, string ExtraInfo);

    public sealed record ShortIssueInfo(string Url, string Title, string CreatedAt, string ClosedAt, bool? Merged, string Author, string Body, string ExtraInfo, int TotalComments, ShortCommentInfo[] RelatedComments);

    public ModelInfo[] AvailableModels { get; } = [new("gpt-4.1", 1_000_000, true), new("o4-mini", 200_000, false)];
    public ModelInfo DefaultModel => AvailableModels[0];

    private Context CreateContext(ModelInfo model, string gitHubUserLogin) => new()
    {
        Parent = this,
        Logger = Logger,
        GitHubDb = GitHubDb,
        Search = Search,
        OpenAI = OpenAI,
        Model = model,
        GitHubUserLogin = gitHubUserLogin
    };

    public IAsyncEnumerable<string> TriageIssueAsync(ModelInfo model, string gitHubUserLogin, IssueInfo issue, Action<string> onToolLog, CancellationToken cancellationToken)
    {
        Context context = CreateContext(model, gitHubUserLogin);
        context.Issue = issue;
        context.OnToolLog = onToolLog;

        return context.TriageAsync(cancellationToken);
    }

    public async Task<ShortIssueInfo> GetCommentHistoryAsync(ModelInfo model, string requesterLogin, int issueOrPRNumber, CancellationToken cancellationToken)
    {
        Context context = CreateContext(model, requesterLogin);

        return await context.GetCommentHistoryAsyncCore(issueOrPRNumber, removeCommentsWithoutContext: false, cancellationToken);
    }

    public async Task<ShortIssueInfo[]> SearchDotnetRuntimeAsync(ModelInfo model, string requesterLogin, string[] searchTerms, string extraSearchContext, IssueSearchFilters filters, CancellationToken cancellationToken)
    {
        Context context = CreateContext(model, requesterLogin);

        return await context.SearchDotnetRuntimeAsyncCore(searchTerms, extraSearchContext, filters, cancellationToken);
    }

    public async Task<IssueInfo> GetIssueAsync(int issueNumber, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        return await GetIssueAsync(issues => issues.Where(i => i.Number == issueNumber), cancellationToken);
    }

    public async Task<IssueInfo> GetIssueAsync(Func<IQueryable<IssueInfo>, IQueryable<IssueInfo>> query, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = GitHubDb.CreateDbContext();

        IQueryable<IssueInfo> issues = db.Issues
            .AsNoTracking();

        issues = query(issues);

        return await issues
            .FromDotnetRuntime()
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
            When referencing an older issue, reference when it was opened. E.g. "Issue #123 (July 2019) - Title".

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
        public Action<string> OnToolLog { get; set; } = _ => { };

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
        private async Task<ShortIssueInfo> GetCommentHistoryAsync(
            [Description("The issue/PR number to get comments for.")] int issueOrPRNumber,
            CancellationToken cancellationToken)
        {
            return await GetCommentHistoryAsyncCore(issueOrPRNumber, removeCommentsWithoutContext: true, cancellationToken);
        }

        public async Task<ShortIssueInfo> GetCommentHistoryAsyncCore(int issueOrPRNumber, bool removeCommentsWithoutContext, CancellationToken cancellationToken)
        {
            IssueInfo issue = await Parent.GetIssueAsync(issueOrPRNumber, cancellationToken);

            if (issue is null)
            {
                OnToolLog($"[Tool] Issue #{issueOrPRNumber} not found.");
                return new ShortIssueInfo("N/A", "N/A", "N/A", "N/A", null, "N/A", "N/A", $"Issue #{issueOrPRNumber} does not appear to exist.", 0, []);
            }

            CommentInfo[] comments = issue.Comments
                .OrderByDescending(c => c.CreatedAt)
                .Where(c => !SemanticMarkdownChunker.IsUnlikelyToBeUseful(issue, c, removeCommentsWithoutContext))
                .ToArray();

            int maxComments = UsingLargeContextWindow ? 200 : 50;

            if (comments.Length > maxComments)
            {
                comments = [.. comments.AsSpan(0, maxComments / 2), .. comments.AsSpan(comments.Length - (maxComments / 2))];
            }

            OnToolLog($"[Tool] Obtained {comments.Length} comments for issue #{issue.Number}: {issue.Title}");

            return CreateIssueInfo(1, issue.CreatedAt, issue.User, issue.Body, issue,
                comments.Select(c => CreateCommentInfo(c.CreatedAt, c.User, c.Body, issue, c)).ToArray());
        }

        [Description(
            "Perform a set of semantic searches over issues and comments in the dotnet/runtime GitHub repository." +
            " Every term represents an independent search.")]
        private async Task<ShortIssueInfo[]> SearchDotnetRuntimeAsync(
            [Description("The set of terms to search for.")] string[] searchTerms,
            [Description("Whether to include open issues/PRs.")] bool includeOpen,
            [Description("Whether to include closed/merged issues/PRs. It's usually useful to include.")] bool includeClosed,
            [Description("Whether to include issues.")] bool includeIssues,
            [Description("Whether to include pull requests.")] bool includePullRequests,
            CancellationToken cancellationToken)
        {
            var filters = new IssueSearchFilters(includeOpen, includeClosed, includeIssues, includePullRequests)
            {
                Repository = "dotnet/runtime"
            };

            return await SearchDotnetRuntimeAsyncCore(searchTerms, $"on issue titled '{Issue.Title}'", filters, cancellationToken);
        }

        public async Task<ShortIssueInfo[]> SearchDotnetRuntimeAsyncCore(string[] searchTerms, string extraSearchContext, IssueSearchFilters filters, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchTerms);

            foreach (string term in searchTerms)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(term);
            }

            extraSearchContext ??= string.Empty;

            OnToolLog($"[Tool] Searching for {string.Join(", ", searchTerms)} ({filters})");

            if ((!filters.IncludeOpen && !filters.IncludeClosed) || (!filters.IncludeIssues && !filters.IncludePullRequests))
            {
                OnToolLog("[Tool] Nothing to search for, returning empty results.");
                return [];
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            List<(IssueSearchResult[] Results, double Score)> searchResults = new();

            int maxTotalResults = UsingLargeContextWindow
                ? Math.Clamp(searchTerms.Length * 50, 75, 200)
                : Math.Clamp(searchTerms.Length * 25, 25, 50);
            int maxResultsPerTerm = searchTerms.Length > 1 ? (UsingLargeContextWindow ? 50 : 25) : maxTotalResults;

            await Parallel.ForEachAsync(searchTerms, async (term, _) =>
            {
                IssueSearchResult[] localResults = (await Search.SearchIssuesAndCommentsAsync(term, maxResultsPerTerm, filters, includeAllIssueComments: true, cancellationToken))
                    .Where(r => r.Score >= 0.20)
                    .Where(r => r.Comment is null || !SemanticMarkdownChunker.IsUnlikelyToBeUseful(r.Issue, r.Comment))
                    .ToArray();

                (IssueSearchResult[] Results, double Score)[] resultsByIssueId = GitHubSearchService.GroupResultsByIssue(localResults);

                try
                {
                    resultsByIssueId = await Search.FilterOutUnrelatedResults(term, extraSearchContext, preferSpeed: false, resultsByIssueId, cancellationToken);
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

            List<ShortIssueInfo> results = [];

            int issueReferences = 0;
            int searchIssues = combinedResults.Length;
            int searchComments = 0;

            foreach (string term in searchTerms)
            {
                string trimmedTerm = term;

                if (trimmedTerm.StartsWith("issue ", StringComparison.OrdinalIgnoreCase))
                {
                    trimmedTerm = term.Substring("issue ".Length);
                }

                if (GitHubHelper.TryParseIssueOrPRNumber(trimmedTerm, dotnetRuntimeOnly: true, out int issueNumber) &&
                    await Parent.GetIssueAsync(issueNumber, cancellationToken) is { } singleIssue)
                {
                    issueReferences++;

                    results.Add(CreateIssueInfo(1, singleIssue.CreatedAt, singleIssue.User, singleIssue.Body, singleIssue, []));
                }
            }

            ShortIssueInfo[] gitHubIssueResults = combinedResults
                .Select(r =>
                {
                    IssueSearchResult[] results = r.Results;
                    IssueInfo issue = results[0].Issue;

                    ShortCommentInfo[] comments = results
                        .Where(r => r.Comment is not null)
                        .Select(r => CreateCommentInfo(r.Comment.CreatedAt, r.Comment.User, r.Comment.Body, r.Issue, r.Comment))
                        .ToArray();

                    searchComments += comments.Length;

                    return CreateIssueInfo(r.Score, issue.CreatedAt, issue.User, issue.Body, issue, comments);
                })
                .Take(maxTotalResults)
                .ToArray();

            results.AddRange(gitHubIssueResults);

            OnToolLog($"[Tool] Found {searchIssues} issues, {searchComments} comments, {results.Count} returned results ({(int)stopwatch.ElapsedMilliseconds} ms)");

            return [.. results];
        }

        private ShortIssueInfo CreateIssueInfo(double score, DateTimeOffset createdAt, UserInfo author, string text, IssueInfo issue, ShortCommentInfo[] comments)
        {
            ShortCommentInfo info = CreateCommentInfo(createdAt, author, text, issue, comment: null);

            string extraInfo = score == 1 ? "" : $"Simmilarity score: {score:F2}";
            if (!string.IsNullOrEmpty(info.ExtraInfo))
            {
                extraInfo = $"{extraInfo}\n{info.ExtraInfo}";
            }

            string closedAt = issue.ClosedAt.HasValue ? issue.ClosedAt.Value.ToISODate() : null;
            bool? merged = issue.PullRequest?.MergedAt.HasValue;

            return new ShortIssueInfo(issue.HtmlUrl, issue.Title, info.CreatedAt, closedAt, merged, info.Author, info.Body, extraInfo, issue.Comments.Count, comments);
        }

        private ShortCommentInfo CreateCommentInfo(DateTimeOffset createdAt, UserInfo author, string text, IssueInfo issue, CommentInfo comment)
        {
            string authorSuffix = s_networkingTeam.Contains(author.Login)
                ? " (member of the .NET networking team)"
                : string.Empty;

            return new ShortCommentInfo(
                createdAt.ToISODate(),
                $"{author.Login}{authorSuffix}",
                SemanticMarkdownChunker.TrimTextToTokens(Search.Tokenizer, text, 2_000),
                FormatReactions(issue, comment));

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
