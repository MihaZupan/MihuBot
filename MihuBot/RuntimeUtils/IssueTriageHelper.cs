using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Markdig.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;
using IssueSearchFilters = MihuBot.RuntimeUtils.GitHubSearchService.IssueSearchFilters;
using IssueSearchResult = MihuBot.RuntimeUtils.GitHubSearchService.IssueSearchResult;
using SearchTimings = MihuBot.RuntimeUtils.GitHubSearchService.SearchTimings;

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

    private Context CreateContext(TriageOptions options)
    {
        Context context = CreateContext(options.Model, options.GitHubUserLogin);
        context.Issue = options.Issue;
        context.OnToolLog = options.OnToolLog;
        context.SkipCommentsOnCurrentIssue = options.SkipCommentsOnCurrentIssue;
        return context;
    }

    public record TriageOptions(ModelInfo Model, string GitHubUserLogin, IssueInfo Issue, Action<string> OnToolLog, bool SkipCommentsOnCurrentIssue);

    public IAsyncEnumerable<string> TriageIssueAsync(TriageOptions options, CancellationToken cancellationToken)
    {
        Context context = CreateContext(options);

        return context.TriageAsync(cancellationToken);
    }

    public async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectDuplicateIssuesAsync(TriageOptions options, CancellationToken cancellationToken)
    {
        Context context = CreateContext(options);

        return await context.DetectDuplicateIssuesAsync(cancellationToken);
    }

    public async Task<ShortIssueInfo> GetCommentHistoryAsync(ModelInfo model, string requesterLogin, int issueOrPRNumber, CancellationToken cancellationToken)
    {
        Context context = CreateContext(model, requesterLogin);

        return await context.GetCommentHistoryAsyncCore(issueOrPRNumber, removeCommentsWithoutContext: false, cancellationToken);
    }

    public async Task<ShortIssueInfo[]> SearchDotnetGitHubAsync(ModelInfo model, string requesterLogin, string[] searchTerms, string extraSearchContext, IssueSearchFilters filters, CancellationToken cancellationToken)
    {
        Context context = CreateContext(model, requesterLogin);

        return await context.SearchDotnetGitHubAsync(searchTerms, extraSearchContext, filters, cancellationToken);
    }

    public async Task<IssueInfo> GetIssueAsync(string repoName, int issueNumber, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        return await GetIssueAsync(issues => issues.Where(i => i.Number == issueNumber && i.Repository.FullName == repoName), cancellationToken);
    }

    public async Task<IssueInfo> GetIssueAsync(Func<IQueryable<IssueInfo>, IQueryable<IssueInfo>> query, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = GitHubDb.CreateDbContext();

        IQueryable<IssueInfo> issues = db.Issues
            .AsNoTracking();

        issues = query(issues);

        issues = AddIssueInfoIncludes(issues);

        return await issues
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static IQueryable<IssueInfo> AddIssueInfoIncludes(IQueryable<IssueInfo> query)
    {
        return query
            .Include(i => i.User)
            .Include(i => i.PullRequest)
            .Include(i => i.Labels)
            .Include(i => i.Repository)
                .ThenInclude(r => r.Owner)
            .Include(i => i.Comments)
                .ThenInclude(c => c.User);
    }

    private sealed class Context
    {
        private const string SystemPromptTemplate =
            """
            You are an assistant helping the .NET team triage new GitHub issues in the {{REPO_URL}} repository.
            You are provided with information about a new issue and you need to find related issues or comments among the existing GitHub data.

            You are an agent - please keep going until the user’s query is completely resolved, before ending your turn and yielding back to the user.
            Assume that the user will NOT be able to reply and ask futher, more refined questions. You MUST iterate and keep going until you have all the information.
            Only terminate your turn when you are sure that the problem is solved.
            Your thinking should be thorough and so it's fine if it's very long. You can think step by step before and after each action you decide to take.

            Use tools to find issues and comments that may be relevant. It is up to you to determine whether the results are relevant.
            Be thorough with your searches and always consider the full discussion on related issues. You should perform as many searches as needed{{MAX_SEARCH_COUNT}}.
            Tools use semantic searching to find issues and comments that may be relevant to the issue you are triaging. You can search for multiple terms at the same time.

            When evaulating an issue, always use tools to ask for the full history of comments on that issue.
            Even if you've seen previous comments from an issue, you haven't seen all of them unless you've called the full history for that specific issue.
            Use your tools to gather the relevant information from comments: do NOT guess or make up conclusions for a given issue.
            Pay close attention to comments when summarizing the issue as questions may have already been answered.

            {{TASK_PROMPT}}

            Reply in GitHub markdown format, not wrapped in any HTML blocks.
            """;

        private const string TriagePrompt =
            """
            Reply with a list of related issues and include a short summary of the discussions/conclusions for each one.
            Assume that the user is familiar with the repository and its processes, but not necessarily with the specific issue or discussion.
            When referencing an older issue, reference when it was opened. E.g. "Issue #123 (July 2019) - Title".
            """;

        private const string DuplicateDetectionPrompt =
            """
            Find which issues (if any) are likely duplicates of the new issue.
            Specify the approximate certainty score from 0 to 1, where 1 means the issue is very likely a duplicate.
            Return the set of issue numbers with their relevance scores. Include a short summary for why you believe the issue is a duplicate.
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
        public bool SkipCommentsOnCurrentIssue { get; set; }

        private Exception _searchToolException;

        public async IAsyncEnumerable<string> TriageAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Logger.DebugLog($"Starting triage for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            OnToolLog($"{Issue.Repository.FullName}#{Issue.Number}: {Issue.Title} by {Issue.User.Login}");

            ChatOptions options = GetTriageOptions();

            string systemPrompt = GetSystemPrompt(TriagePrompt);

            List<ChatMessage> messages = [new ChatMessage(ChatRole.System, systemPrompt)];

            string existingCommentsMention = SkipCommentsOnCurrentIssue
                ? string.Empty
                : $"\nExisting comments: {Issue.Comments.Count(c => !c.User.Login.EndsWith("[bot]", StringComparison.Ordinal))}";

            messages.Add(new ChatMessage(ChatRole.User,
                $"""
                Please help me triage issue #{Issue.Number} from {Issue.User.Login} titled '{Issue.Title}'.{existingCommentsMention}

                Here is the issue:
                {Issue.Body}
                """));

            using FunctionInvokingChatClient toolClient = GetToolClient();

            string markdownResponse = "";

            await foreach (ChatResponseUpdate update in toolClient.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                if (_searchToolException is not null)
                {
                    break;
                }

                string updateText = update.Text;

                if (string.IsNullOrEmpty(updateText))
                {
                    continue;
                }

                markdownResponse += updateText;

                yield return ConvertMarkdownToHtml(markdownResponse, partial: true);
            }

            if (_searchToolException is not null)
            {
                throw new Exception($"Search tool failed to return results: {_searchToolException}");
            }

            Logger.DebugLog($"Triage: Finished triaging issue #{Issue.Number} with model {Model.Name}:\n{markdownResponse}");

            yield return ConvertMarkdownToHtml(markdownResponse, partial: false);
        }

        private sealed record DuplicateIssue(int IssueNumber, double Certainty, string Summary);

        public async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectDuplicateIssuesAsync(CancellationToken cancellationToken)
        {
            Logger.DebugLog($"Starting duplicate detection for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            ChatOptions options = GetTriageOptions();

            using FunctionInvokingChatClient toolClient = GetToolClient();

            string prompt =
                $"""
                {GetSystemPrompt(DuplicateDetectionPrompt)}

                Please help me find potential duplicates for issue #{Issue.Number} from {Issue.User.Login} titled '{Issue.Title}'.

                Here is the issue:
                {Issue.Body}
                """;

            ChatResponse<DuplicateIssue[]> chatResponse = await toolClient.GetResponseAsync<DuplicateIssue[]>(prompt, options, useJsonSchemaResponseFormat: true, cancellationToken);

            if (_searchToolException is not null)
            {
                throw new Exception($"Search tool failed to return results: {_searchToolException}");
            }

            List<(IssueInfo Issue, double Certainty, string Summary)> results = [];

            foreach (DuplicateIssue duplicate in chatResponse.Result.OrderByDescending(r => r.Certainty))
            {
                IssueInfo issue = await Parent.GetIssueAsync(Issue.Repository.FullName, duplicate.IssueNumber, cancellationToken);

                if (issue is not null)
                {
                    results.Add((issue, duplicate.Certainty, duplicate.Summary));
                }
            }

            Logger.DebugLog($"Finished duplicate detection on issue #{Issue.Number} with model {Model.Name}:\n{JsonSerializer.Serialize(results)}");

            return [.. results];
        }

        private string GetSystemPrompt(string taskPrompt)
        {
            return SystemPromptTemplate
                .Replace("{{REPO_URL}}", Issue.Repository.HtmlUrl, StringComparison.Ordinal)
                .Replace("{{MAX_SEARCH_COUNT}}", UsingLargeContextWindow ? "" : " (max of 5)", StringComparison.Ordinal)
                .Replace("{{TASK_PROMPT}}", taskPrompt, StringComparison.Ordinal);
        }

        private ChatOptions GetTriageOptions()
        {
            var options = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(SearchCurrentRepoAsync,
                        $"search_{Issue.RepoOwner()}_{Issue.RepoName()}",
                        $"Perform a set of semantic searches over issues and comments in the {Issue.Repository.FullName} GitHub repository. Every term represents an independent search."),

                    AIFunctionFactory.Create(GetCommentHistoryAsync,
                        "get_github_comment_history",
                        $"Get the full history of comments on a specific issue or pull request from the {Issue.Repository.FullName} GitHub repository."),
                ],
                ToolMode = ChatToolMode.RequireAny
            };

            if (Model.SupportsTemperature)
            {
                options.Temperature = 0;
            }

            return options;
        }

        private FunctionInvokingChatClient GetToolClient()
        {
            IChatClient chatClient = OpenAI.GetChat(Model.Name, secondary: true);

            return new FunctionInvokingChatClient(chatClient)
            {
                AllowConcurrentInvocation = true,
                MaximumIterationsPerRequest = 20,
            };
        }

        private string ConvertMarkdownToHtml(string markdown, bool partial)
        {
            MarkdownDocument document = MarkdownHelper.ParseAdvanced(markdown);

            if (partial)
            {
                MarkdownHelper.FixUpPartialDocument(document);
            }

            MarkdownHelper.ReplaceGitHubIssueReferencesWithLinks(document, Issue.Repository.FullName);

            MarkdownHelper.ReplaceGitHubUserMentionsWithLinks(document);

            return document.ToHtmlAdvanced();
        }

        private async Task<ShortIssueInfo> GetCommentHistoryAsync(
            [Description("The issue/PR number to get comments for.")] int issueOrPRNumber,
            CancellationToken cancellationToken)
        {
            return await GetCommentHistoryAsyncCore(issueOrPRNumber, removeCommentsWithoutContext: true, cancellationToken);
        }

        public async Task<ShortIssueInfo> GetCommentHistoryAsyncCore(int issueOrPRNumber, bool removeCommentsWithoutContext, CancellationToken cancellationToken)
        {
            IssueInfo issue = await Parent.GetIssueAsync(Issue.Repository.FullName, issueOrPRNumber, cancellationToken);

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

        private async Task<ShortIssueInfo[]> SearchCurrentRepoAsync(
            [Description("The set of terms to search for.")] string[] searchTerms,
            [Description("Whether to include open issues/PRs.")] bool includeOpen = true,
            [Description("Whether to include closed/merged issues/PRs. It's usually useful to include.")] bool includeClosed = true,
            [Description("Whether to include issues.")] bool includeIssues = true,
            [Description("Whether to include pull requests.")] bool includePullRequests = true,
            CancellationToken cancellationToken = default)
        {
            var filters = new IssueSearchFilters(includeOpen, includeClosed, includeIssues, includePullRequests)
            {
                Repository = Issue.Repository.FullName,
            };

            try
            {
                return await SearchDotnetGitHubAsync(searchTerms, $"on issue titled '{Issue.Title}'", filters, cancellationToken);
            }
            catch (Exception ex)
            {
                _searchToolException = ex;
                throw;
            }
        }

        public async Task<ShortIssueInfo[]> SearchDotnetGitHubAsync(string[] searchTerms, string extraSearchContext, IssueSearchFilters filters, CancellationToken cancellationToken)
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

            filters.PostFilter = result =>
                result.Score >= 0.20 &&
                (result.Comment is null || !SemanticMarkdownChunker.IsUnlikelyToBeUseful(result.Issue, result.Comment));

            Stopwatch stopwatch = Stopwatch.StartNew();

            List<(IssueSearchResult[] Results, double Score)> searchResults = new();

            int maxTotalResults = UsingLargeContextWindow
                ? Math.Clamp(searchTerms.Length * 50, 75, 200)
                : Math.Clamp(searchTerms.Length * 25, 25, 50);
            int maxResultsPerTerm = searchTerms.Length > 1 ? (UsingLargeContextWindow ? 50 : 25) : maxTotalResults;

            await Parallel.ForEachAsync(searchTerms, async (term, _) =>
            {
                ((IssueSearchResult[] Results, double Score)[] results, SearchTimings timings) = await Search.SearchIssuesAndCommentsAsync(term, maxResultsPerTerm, filters, includeAllIssueComments: true, cancellationToken);

                if (SkipCommentsOnCurrentIssue)
                {
                    results = results
                        .Select(r =>
                        {
                            if (r.Results[0].Issue.Id == Issue.Id)
                            {
                                // Skip comments on the current issue.
                                return (Results: r.Results.Where(c => c.Comment is null).ToArray(), r.Score);
                            }

                            return r;
                        })
                        .Where(r => r.Results.Length > 0)
                        .ToArray();
                }

                try
                {
                    results = await Search.FilterOutUnrelatedResults(term, extraSearchContext, preferSpeed: false, results, timings, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.DebugLog($"Triage: Error filtering unrelated results: {ex.Message}");
                }

                lock (searchResults)
                {
                    searchResults.AddRange(results);
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

            int searchIssues = combinedResults.Length;
            int searchComments = 0;

            foreach (string term in searchTerms)
            {
                string trimmedTerm = term;

                if (trimmedTerm.StartsWith("issue ", StringComparison.OrdinalIgnoreCase))
                {
                    trimmedTerm = term.Substring("issue ".Length);
                }

                if (GitHubHelper.TryParseIssueOrPRNumber(trimmedTerm, out string repoName, out int issueNumber) &&
                    await Parent.GetIssueAsync(repoName ?? "dotnet/runtime", issueNumber, cancellationToken) is { } singleIssue)
                {
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
