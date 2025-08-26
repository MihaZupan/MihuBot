using System.ComponentModel;
using System.Globalization;
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
    public sealed record ShortCommentInfo(string CreatedAt, string Author, string Body);

    public sealed record ShortIssueInfo(string Url, string Title, string CreatedAt, string ClosedAt, bool? Merged, string Author, string Body, ShortCommentInfo[] Comments);

    public ModelInfo[] AvailableModels => OpenAIService.AllModels;
    public ModelInfo DefaultModel => AvailableModels.First(m => m.Name == "gpt-4.1");

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
            Specify the approximate certainty score from 0 to 1, where 1 means the issue is almost certainly a duplicate.
            Return the set of issue numbers with their relevance scores. Include a short summary for why you believe the issue is a duplicate.
            The summary should be a single paragraph, in GitHub markdown format.

            You are specifically only looking for issues that are very likely duplicates of the new issue, not just related ones.
            If an issue is related, but not a duplicate, do not include it in the results.

            For example if two issues both report a failure in the same test file, but for a different test, or with a different error message, that is not a duplicate.
            If two issues report an error with the same API/method, but with different errors, that is not a duplicate.

            If there is insufficient information to make a reliable determination, do not report the issue as a duplicate.
            Focus on making an accurate decision. Mistakingly reporting an issue as a duplicate is worse than not reporting it at all.
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
        public bool UsingLargeContextWindow => Model.ContextSize >= 400_000;
        public Action<string> OnToolLog { get; set; } = _ => { };
        public bool SkipCommentsOnCurrentIssue { get; set; }

        private Exception _searchToolException;

        private int MaxResultsPerTerm => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(MaxResultsPerTerm)}", 20);
        private int MaxResultsPerTermLarge => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(MaxResultsPerTermLarge)}", 40);
        private int SearchMaxTotalResults => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(SearchMaxTotalResults)}", 40);
        private int SearchMaxTotalResultsLarge => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(SearchMaxTotalResultsLarge)}", 60);
        private float SearchMinCertainty => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(SearchMinCertainty)}", 0.2f);
        private bool SearchIncludeAllIssueComments => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(SearchIncludeAllIssueComments)}", true);
        private bool AdvertiseCommentsTool => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.{nameof(AdvertiseCommentsTool)}", false);
        private int MaxCommentsPerIssue => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.{nameof(MaxCommentsPerIssue)}", 20);

        public async IAsyncEnumerable<string> TriageAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Logger.DebugLog($"Starting triage for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            OnToolLog($"{Issue.Repository.FullName}#{Issue.Number}: {Issue.Title} by {Issue.User.Login}");

            ChatOptions options = GetTriageOptions();

            string systemPrompt = GetSystemPrompt(TriagePrompt);

            List<ChatMessage> messages = [new ChatMessage(ChatRole.System, systemPrompt)];

            string existingCommentsMention = SkipCommentsOnCurrentIssue
                ? string.Empty
                : $"\nExisting comments: {Issue.Comments.Count(c => c.User.IsLikelyARealUser())}";

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

        private sealed record DuplicateIssue(int IssueNumber, double? Certainty, string Summary);

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
                if (duplicate.IssueNumber <= 0 || duplicate.IssueNumber == Issue.Number)
                {
                    continue;
                }

                IssueInfo issue = await Parent.GetIssueAsync(Issue.Repository.FullName, duplicate.IssueNumber, cancellationToken);

                if (issue is not null && duplicate.Certainty.HasValue && duplicate.Certainty > 0)
                {
                    results.Add((issue, duplicate.Certainty.Value, duplicate.Summary));
                }
            }

            Logger.DebugLog($"Finished duplicate detection on issue #{Issue.Number} with model {Model.Name}:\n{JsonSerializer.Serialize(chatResponse.Result)}");

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
            List<AITool> tools =
            [
                AIFunctionFactory.Create(SearchCurrentRepoAsync,
                    $"search_{Issue.RepoOwner()}_{Issue.RepoName()}",
                    $"Perform a set of semantic searches over issues and comments in the {Issue.Repository.FullName} GitHub repository. Every term represents an independent search."),
            ];

            if (AdvertiseCommentsTool)
            {
                tools.Add(AIFunctionFactory.Create(GetCommentHistoryAsync,
                    "get_github_comment_history",
                    $"Get the full history of comments on a specific issue or pull request from the {Issue.Repository.FullName} GitHub repository."));
            }

            var options = new ChatOptions
            {
                Tools = tools,
                ToolMode = ChatToolMode.RequireAny
            };

            if (Model.SupportsTemperature)
            {
                options.Temperature = Search.DefaultTemperature;
            }

            return options;
        }

        private FunctionInvokingChatClient GetToolClient()
        {
            IChatClient chatClient = OpenAI.GetChat(Model.Name, secondary: true);

            chatClient = new Helpers.LoggingChatClient(chatClient, Logger, Search._configuration);

            return new FunctionInvokingChatClient(chatClient)
            {
                AllowConcurrentInvocation = true,
                MaximumIterationsPerRequest = 20,
            };
        }

        private string ConvertMarkdownToHtml(string markdown, bool partial)
        {
            markdown = MarkdownHelper.ReplaceGitHubUserMentionsWithLinks(markdown);

            MarkdownDocument document = MarkdownHelper.ParseAdvanced(markdown);

            if (partial)
            {
                MarkdownHelper.FixUpPartialDocument(document);
            }

            MarkdownHelper.ReplaceGitHubIssueReferencesWithLinks(document, Issue.Repository.FullName);

            if (MarkdownHelper.ContainsGitHubUserMentions(document))
            {
                Logger.DebugLog($"Found GitHub mention after processing.\n{markdown}");
                throw new Exception("Still contains GH mentions");
            }

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
                return new ShortIssueInfo("N/A", "N/A", "N/A", "N/A", null, "N/A", $"Issue #{issueOrPRNumber} does not appear to exist.", []);
            }

            ShortCommentInfo[] comments = CreateCommentInfos(issue, issueOrPRNumber, removeCommentsWithoutContext, includeAll: true);

            return CreateIssueInfo(issue.CreatedAt, issue.User, issue.Body, issue, comments);
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

            if (searchTerms.Length == 0 || (!filters.IncludeOpen && !filters.IncludeClosed) || (!filters.IncludeIssues && !filters.IncludePullRequests))
            {
                OnToolLog("[Tool] Nothing to search for, returning empty results.");
                return [];
            }

            filters.PostFilter = result =>
                result.Score >= 0.20 &&
                (result.Comment is null || !SemanticMarkdownChunker.IsUnlikelyToBeUseful(result.Issue, result.Comment));

            Stopwatch stopwatch = Stopwatch.StartNew();

            List<(IssueSearchResult[] Results, double Score)> searchResults = new();

            int maxResultsPerTerm = UsingLargeContextWindow ? MaxResultsPerTermLarge : MaxResultsPerTerm;
            int maxTotalResults = Math.Min(searchTerms.Length * maxResultsPerTerm, UsingLargeContextWindow ? SearchMaxTotalResultsLarge : SearchMaxTotalResults);

            bool skipCommentsOnCurrentIssue = SkipCommentsOnCurrentIssue;

            await Parallel.ForEachAsync(searchTerms, async (term, _) =>
            {
                ((IssueSearchResult[] Results, double Score)[] results, SearchTimings timings) = await Search.SearchIssuesAndCommentsAsync(term, maxResultsPerTerm, filters, includeAllIssueComments: true, cancellationToken);

                if (skipCommentsOnCurrentIssue)
                {
                    results = [.. results.Where(r => r.Results[0].Issue.Id != Issue.Id)];
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

            float minCertainty = SearchMinCertainty;
            bool includeAllComments = SearchIncludeAllIssueComments;

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
                    ShortCommentInfo[] comments = includeAllComments
                        ? CreateCommentInfos(singleIssue, singleIssue.Number, removeCommentsWithoutContext: true)
                        : [];

                    results.Add(CreateIssueInfo(singleIssue.CreatedAt, singleIssue.User, singleIssue.Body, singleIssue, comments));
                }
            }

            ShortIssueInfo[] gitHubIssueResults = combinedResults
                .Where(r => r.Score >= minCertainty)
                .Where(r => !skipCommentsOnCurrentIssue || r.Results[0].Issue.Id != Issue.Id)
                .Select(r =>
                {
                    IssueSearchResult[] results = r.Results;
                    IssueInfo issue = results[0].Issue;

                    ShortCommentInfo[] comments = [];

                    CommentInfo[] relevantComments = results
                        .Where(r => r.Comment is not null)
                        .Select(r => r.Comment)
                        .ToArray();

                    if (includeAllComments)
                    {
                        comments = CreateCommentInfos(issue, issue.Number, removeCommentsWithoutContext: true, mergeWith: relevantComments);
                    }
                    else
                    {
                        comments = [.. relevantComments.Select(c => CreateCommentInfo(c.CreatedAt, c.User, c.Body))];
                    }

                    searchComments += comments.Length;

                    return CreateIssueInfo(issue.CreatedAt, issue.User, issue.Body, issue, comments);
                })
                .Take(maxTotalResults)
                .ToArray();

            results.AddRange(gitHubIssueResults);

            OnToolLog($"[Tool] Found {searchIssues} issues, {searchComments} comments, {results.Count} returned results ({(int)stopwatch.ElapsedMilliseconds} ms)");

            return [.. results];
        }

        private ShortIssueInfo CreateIssueInfo(DateTimeOffset createdAt, UserInfo author, string text, IssueInfo issue, ShortCommentInfo[] comments)
        {
            ShortCommentInfo info = CreateCommentInfo(createdAt, author, text, isComment: false);

            //string extraInfo = score == 1 ? "" : $"Simmilarity score: {score:F2}";
            //if (!string.IsNullOrEmpty(info.ExtraInfo))
            //{
            //    extraInfo = $"{extraInfo}\n{info.ExtraInfo}";
            //}

            string closedAt = issue.ClosedAt.HasValue ? issue.ClosedAt.Value.ToISODate() : null;
            bool? merged = issue.PullRequest?.MergedAt.HasValue;

            return new ShortIssueInfo(issue.HtmlUrl, issue.Title, info.CreatedAt, closedAt, merged, info.Author, info.Body, comments);
        }

        private ShortCommentInfo CreateCommentInfo(DateTimeOffset createdAt, UserInfo author, string text, bool isComment = true)
        {
            string authorSuffix = s_networkingTeam.Contains(author.Login)
                ? " (member of the .NET networking team)"
                : string.Empty;

            int maxLength = isComment ? 1_000 : 2_000;

            if (UsingLargeContextWindow)
            {
                maxLength *= 2;
            }

            return new ShortCommentInfo(
                createdAt.ToISODate(),
                $"{author.Login}{authorSuffix}",
                SemanticMarkdownChunker.TrimTextToTokens(Search.Tokenizer, text, maxLength));

            //static string FormatReactions(IssueInfo issue, CommentInfo comment)
            //{
            //    int positive =
            //        (comment?.Plus1 ?? issue.Plus1) +
            //        (comment?.Heart ?? issue.Heart) +
            //        (comment?.Hooray ?? issue.Hooray) +
            //        (comment?.Rocket ?? issue.Rocket);

            //    int negative =
            //        (comment?.Minus1 ?? issue.Minus1) +
            //        (comment?.Confused ?? issue.Confused);

            //    if (positive > 0 || negative > 0)
            //    {
            //        return $"Community reacted with {positive} upvotes, {negative} downvotes";
            //    }

            //    return string.Empty;
            //}
        }

        private ShortCommentInfo[] CreateCommentInfos(IssueInfo issue, int issueOrPRNumber, bool removeCommentsWithoutContext, bool includeAll = false, CommentInfo[] mergeWith = null)
        {
            CommentInfo[] comments = issue.Comments
                .OrderBy(c => c.CreatedAt)
                .Where(c => !SemanticMarkdownChunker.IsUnlikelyToBeUseful(issue, c, removeCommentsWithoutContext))
                .ToArray();

            if (SkipCommentsOnCurrentIssue && issueOrPRNumber == Issue.Number)
            {
                comments = [];
            }

            int maxComments = includeAll ? int.MaxValue : MaxCommentsPerIssue;

            if (comments.Length > maxComments)
            {
                comments = [.. comments.AsSpan(0, maxComments / 2), .. comments.AsSpan(comments.Length - (maxComments / 2))];
            }

            if (mergeWith is not null)
            {
                comments = comments.Concat(mergeWith)
                    .DistinctBy(c => c.Id)
                    .OrderBy(c => c.CreatedAt)
                    .ToArray();
            }

            OnToolLog($"[Tool] Obtained {comments.Length} comments for issue #{issue.Number}: {issue.Title}");

            return [.. comments.Select(c => CreateCommentInfo(c.CreatedAt, c.User, c.Body))];
        }
    }
}
