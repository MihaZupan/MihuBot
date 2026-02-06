using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Markdig.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.Search;

namespace MihuBot.RuntimeUtils.AI;

public sealed class IssueTriageHelper(Logger Logger, IDbContextFactory<GitHubDbContext> GitHubDb, GitHubSearchService Search, OpenAIService OpenAI)
{
    public ModelInfo[] AvailableModels => OpenAIService.AllModels;
    public ModelInfo DefaultModel => AvailableModels.First(m => m.Name == "gpt-5-mini");

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

    public async Task<IssueInfoForPrompt[]> SearchDotnetGitHubAsync(ModelInfo model, string requesterLogin, string[] searchTerms, string extraSearchContext, IssueSearchFilters filters, CancellationToken cancellationToken)
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

        private const string SearchQueryExtractionPrompt =
            """
            You are an assistant helping find potential duplicate GitHub issues.
            Given a new issue, extract a set of semantic search queries that would help find existing issues that describe the same problem.

            Focus on:
            - The core problem or error being described
            - Specific APIs, types, methods, or error messages mentioned
            - The scenario or use case

            Return between 2 and 6 search queries. Each query should be a short phrase or sentence capturing a distinct aspect of the issue.
            Do not include issue numbers or URLs. Do not include generic terms like "bug" or "issue".
            """;

        private const string CandidateClassificationPrompt =
            """
            You are an assistant helping detect duplicate GitHub issues.
            You will be given a NEW issue and one CANDIDATE issue that may be a duplicate.

            Determine whether the candidate issue describes the same problem as the new issue.
            Specify a certainty score from 0 to 1, where 1 means the candidate is almost certainly a duplicate.

            You are specifically looking for issues that are very likely duplicates, not just related ones.
            If an issue is related but not a duplicate, give it a low score.

            For example if two issues both report a failure in the same test file, but for a different test, or with a different error message, that is not a duplicate.
            If two issues report an error with the same API/method, but with different errors, that is not a duplicate.

            If there is insufficient information to make a reliable determination, give a low score.
            Focus on making an accurate decision. Mistakenly reporting an issue as a duplicate is worse than not reporting it at all.

            Include a short summary (single paragraph, GitHub markdown format) explaining why you believe the candidate is or is not a duplicate.
            """;

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
        private int MaxCommentsPerIssue => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.{nameof(MaxCommentsPerIssue)}", 20);

        public async IAsyncEnumerable<string> TriageAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Logger.DebugLog($"Starting triage for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            OnToolLog($"{Issue.Repository.FullName}#{Issue.Number}: {Issue.Title} by {Issue.User.Login}");

            ChatOptions options = GetTriageOptions();

            string systemPrompt = GetSystemPrompt(TriagePrompt);

            List<ChatMessage> messages = [new ChatMessage(ChatRole.System, systemPrompt)];

            messages.Add(new ChatMessage(ChatRole.User,
                $"""
                Please help me triage the following issue:

                ```json
                {(await IssueInfoForPrompt.CreateAsync(Issue, GitHubDb, cancellationToken)).AsJson()}
                ```
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

        private sealed record SearchQueries(string[] Queries);

        private sealed record CandidateClassification(double? Certainty, string Summary);

        public async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectDuplicateIssuesAsync(CancellationToken cancellationToken)
        {
            Logger.DebugLog($"Starting duplicate detection for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            // Step 1: Extract search queries from the new issue
            string[] searchQueries = await ExtractSearchQueriesAsync(cancellationToken);

            OnToolLog($"Extracted {searchQueries.Length} search queries: {string.Join(", ", searchQueries)}");

            if (searchQueries.Length == 0)
            {
                return [];
            }

            // Step 2: Semantic search for candidate issues
            var filters = new IssueSearchFilters
            {
                IncludeOpen = true,
                IncludeClosed = true,
                IncludeIssues = true,
                IncludePullRequests = true,
                Repository = Issue.Repository.FullName,
                MinScore = SearchMinCertainty,
            };

            var bulkFilters = new IssueSearchBulkFilters
            {
                MaxResultsPerTerm = MaxResultsPerTerm,
                ExcludeIssues = SkipCommentsOnCurrentIssue ? [Issue] : null,
                PostProcessIssues = true,
                PostProcessingContext = $"{IssueSearchBulkFilters.DefaultPostProcessingContext} on issue titled '{Issue.Title}'"
            };

            var options = new IssueSearchResponseOptions
            {
                IncludeIssueComments = SearchIncludeAllIssueComments,
                MaxResults = SearchMaxTotalResults,
                PreferSpeed = false,
            };

            GitHubSearchResponse searchResults = await Search.SearchIssuesAndCommentsAsync(searchQueries, bulkFilters, filters, options, cancellationToken);

            var candidates = searchResults.Results
                .Where(r => r.Score >= 0.3 && r.Results[0].Issue.Id != Issue.Id)
                .OrderByDescending(r => r.Score)
                .Take(15)
                .ToArray();

            OnToolLog($"Found {candidates.Length} candidate issues to classify");

            if (candidates.Length == 0)
            {
                return [];
            }

            // Step 3: Classify each candidate independently
            string issueJson = (await IssueInfoForPrompt.CreateAsync(Issue, GitHubDb, cancellationToken, contextLimitForIssueBody: 4000)).AsJson();

            var classificationTasks = candidates.Select(async candidate =>
            {
                var candidateIssue = candidate.Results[0].Issue;
                var classification = await ClassifyCandidateAsync(issueJson, candidateIssue, cancellationToken);
                return (Issue: candidateIssue, Certainty: classification.Certainty ?? 0, classification.Summary);
            });

            var results = await Task.WhenAll(classificationTasks);

            Logger.DebugLog($"Finished duplicate detection on issue #{Issue.Number} with model {Model.Name}:\n{JsonSerializer.Serialize(results.Select(r => new { r.Issue.Number, r.Certainty, r.Summary }))}");

            return results
                .Where(r => r.Certainty > 0)
                .GroupBy(r => r.Issue.Id)
                .Select(g => g.MaxBy(i => i.Certainty))
                .OrderByDescending(i => i.Certainty)
                .ToArray();
        }

        private async Task<string[]> ExtractSearchQueriesAsync(CancellationToken cancellationToken)
        {
            IChatClient chatClient = OpenAI.GetChat(Model.Name, secondary: true);

            string prompt =
                $"""
                {SearchQueryExtractionPrompt}

                New issue #{Issue.Number} from {Issue.User.Login} titled '{Issue.Title}' in {Issue.Repository.FullName}:

                {Issue.Body}
                """;

            var chatOptions = new ChatOptions();
            if (Model.SupportsTemperature)
            {
                chatOptions.Temperature = Search.DefaultTemperature;
            }

            ChatResponse<SearchQueries> response = await chatClient.GetResponseAsync<SearchQueries>(prompt, chatOptions, useJsonSchemaResponseFormat: true, cancellationToken);

            return response.Result?.Queries ?? [];
        }

        private async Task<CandidateClassification> ClassifyCandidateAsync(string newIssueJson, IssueInfo candidate, CancellationToken cancellationToken)
        {
            IChatClient chatClient = OpenAI.GetChat(Model.Name, secondary: true);

            string candidateJson = (await IssueInfoForPrompt.CreateAsync(candidate, GitHubDb, cancellationToken, contextLimitForIssueBody: 4000, contextLimitForCommentBody: 2000)).AsJson();

            string prompt =
                $"""
                {CandidateClassificationPrompt}

                NEW ISSUE:
                ```json
                {newIssueJson}
                ```

                CANDIDATE ISSUE:
                ```json
                {candidateJson}
                ```
                """;

            var chatOptions = new ChatOptions();
            if (Model.SupportsTemperature)
            {
                chatOptions.Temperature = Search.DefaultTemperature;
            }

            ChatResponse<CandidateClassification> response = await chatClient.GetResponseAsync<CandidateClassification>(prompt, chatOptions, useJsonSchemaResponseFormat: true, cancellationToken);

            return response.Result ?? new CandidateClassification(0, string.Empty);
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

        private async Task<IssueInfoForPrompt[]> SearchCurrentRepoAsync(
            [Description("The set of terms to search for.")] string[] searchTerms,
            [Description("Whether to include open issues/PRs.")] bool includeOpen = true,
            [Description("Whether to include closed/merged issues/PRs. It's usually useful to include.")] bool includeClosed = true,
            [Description("Whether to include issues.")] bool includeIssues = true,
            [Description("Whether to include pull requests.")] bool includePullRequests = true,
            CancellationToken cancellationToken = default)
        {
            var filters = new IssueSearchFilters
            {
                IncludeOpen = includeOpen,
                IncludeClosed = includeClosed,
                IncludeIssues = includeIssues,
                IncludePullRequests = includePullRequests,
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

        public async Task<IssueInfoForPrompt[]> SearchDotnetGitHubAsync(string[] searchTerms, string extraSearchContext, IssueSearchFilters filters, CancellationToken cancellationToken)
        {
            OnToolLog($"[Tool] Searching for {string.Join(", ", searchTerms)} ({filters})");

            Stopwatch stopwatch = Stopwatch.StartNew();

            int maxResultsPerTerm = UsingLargeContextWindow ? MaxResultsPerTermLarge : MaxResultsPerTerm;
            int maxTotalResults = Math.Min(searchTerms.Length * maxResultsPerTerm, UsingLargeContextWindow ? SearchMaxTotalResultsLarge : SearchMaxTotalResults);

            var bulkFilters = new IssueSearchBulkFilters
            {
                MaxResultsPerTerm = maxResultsPerTerm,
                ExcludeIssues = SkipCommentsOnCurrentIssue ? [Issue] : null,
                PostProcessIssues = true,
                PostProcessingContext = $"{IssueSearchBulkFilters.DefaultPostProcessingContext} {extraSearchContext}"
            };

            var options = new IssueSearchResponseOptions
            {
                IncludeIssueComments = filters.IncludeCommentsInResponse ?? SearchIncludeAllIssueComments,
                MaxResults = maxTotalResults,
                PreferSpeed = false,
            };

            filters.MinScore = SearchMinCertainty;

            GitHubSearchResponse results = await Search.SearchIssuesAndCommentsAsync(searchTerms, bulkFilters, filters, options, cancellationToken);

            IssueInfoForPrompt[] issues = await results.Results
                .ToAsyncEnumerable()
                .Select(async (i, ct) => await IssueInfoForPrompt.CreateAsync(i.Issue, GitHubDb, ct, contextLimitForIssueBody: 4000, contextLimitForCommentBody: 2000))
                .ToArrayAsync(cancellationToken);

            OnToolLog($"[Tool] Found {issues.Length} issues, {issues.Sum(i => i.Comments.Length)} comments ({(int)stopwatch.ElapsedMilliseconds} ms)");

            return issues;
        }
    }
}
