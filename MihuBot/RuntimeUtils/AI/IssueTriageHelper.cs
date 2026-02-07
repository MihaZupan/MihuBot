using System.Runtime.CompilerServices;
using System.Text.Json;
using Markdig.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.Search;
using OpenAI.Chat;

namespace MihuBot.RuntimeUtils.AI;

public sealed class IssueTriageHelper(Logger Logger, IDbContextFactory<GitHubDbContext> GitHubDb, GitHubSearchService Search, OpenAIService OpenAI)
{
    public ModelInfo[] AvailableModels => OpenAIService.AllModels;
    public ModelInfo DefaultModel => AvailableModels.First(m => m.Name == OpenAIService.DefaultModel);

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
        context.AllowReasoning = options.AllowReasoning;
        return context;
    }

    public record TriageOptions(ModelInfo Model, string GitHubUserLogin, IssueInfo Issue, Action<string> OnToolLog, bool SkipCommentsOnCurrentIssue, bool AllowReasoning = false);

    public IAsyncEnumerable<string> TriageIssueAsync(TriageOptions options, CancellationToken cancellationToken)
    {
        Context context = CreateContext(options);

        return context.TriageAsync(cancellationToken);
    }

    public async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectDuplicateIssuesAsync(TriageOptions options, CancellationToken cancellationToken, IssueInfo[] candidateIssues = null)
    {
        Context context = CreateContext(options);

        return await context.DetectDuplicateIssuesAsync(cancellationToken, candidateIssues);
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
        private const string SearchQueryExtractionPrompt =
            """
            You are an assistant helping find related GitHub issues.
            Given a new issue, extract a set of semantic search queries that would help find existing issues that may be related or describe the same problem.

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

        private const string TriageSynthesisPrompt =
            """
            You are an assistant helping the .NET team triage new GitHub issues.
            You are given information about a new issue and a set of potentially related existing issues found via semantic search.

            Your task is to review the related issues and produce a summary of relevant findings.
            Reply with a list of related issues and include a short summary of the discussions/conclusions for each one.
            Assume that the user is familiar with the repository and its processes, but not necessarily with the specific issue or discussion.
            When referencing an older issue, reference when it was opened. E.g. "Issue #123 (July 2019) - Title".

            If none of the candidate issues are relevant, say so briefly.

            Reply in GitHub markdown format, not wrapped in any HTML blocks.
            """;

        public IssueTriageHelper Parent { get; set; }
        public Logger Logger { get; set; }
        public IDbContextFactory<GitHubDbContext> GitHubDb { get; set; }
        public GitHubSearchService Search { get; set; }
        public OpenAIService OpenAI { get; set; }
        public IssueInfo Issue { get; set; }
        public ModelInfo Model { get; set; }
        public string GitHubUserLogin { get; set; }
        public Action<string> OnToolLog { get; set; } = _ => { };
        public bool SkipCommentsOnCurrentIssue { get; set; }
        public bool AllowReasoning { get; set; }

        private ChatOptions ReasoningChatOptions => AllowReasoning
            ? new ChatOptions
            {
                RawRepresentationFactory = _ => new ChatCompletionOptions
                {
                    ReasoningEffortLevel = ChatReasoningEffortLevel.Medium,
                },
            }
            : null;

        private int MaxResultsPerTerm => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(MaxResultsPerTerm)}", 25);
        private int SearchMaxTotalResults => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(SearchMaxTotalResults)}", 40);
        private float SearchMinCertainty => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(SearchMinCertainty)}", 0.2f);
        private bool SearchIncludeAllIssueComments => Search._configuration.GetOrDefault(null, $"{nameof(IssueTriageHelper)}.Search.{nameof(SearchIncludeAllIssueComments)}", true);

        public async IAsyncEnumerable<string> TriageAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Logger.DebugLog($"Starting triage for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            OnToolLog($"{Issue.Repository.FullName}#{Issue.Number}: {Issue.Title} by {Issue.User.Login}");

            // Step 1: Extract search queries from the new issue
            string[] searchQueries = await ExtractSearchQueriesAsync(cancellationToken);

            OnToolLog($"Extracted {searchQueries.Length} search queries: {string.Join(", ", searchQueries)}");

            if (searchQueries.Length == 0)
            {
                string noResults = "No search queries could be extracted from the issue.";
                yield return ConvertMarkdownToHtml(noResults, partial: false);
                yield break;
            }

            // Step 2: Semantic search for related issues
            IssueResultGroup[] candidates = await SearchForRelatedIssuesAsync(searchQueries, cancellationToken);

            OnToolLog($"Found {candidates.Length} candidate issues");

            if (candidates.Length == 0)
            {
                string noResults = "No related issues found.";
                yield return ConvertMarkdownToHtml(noResults, partial: false);
                yield break;
            }

            // Step 3: Synthesize a triage summary from the results
            string issueJson = (await IssueInfoForPrompt.CreateAsync(Issue, GitHubDb, cancellationToken)).AsJson();

            IssueInfoForPrompt[] candidateInfos = await candidates
                .ToAsyncEnumerable()
                .Select(async (c, ct) => await IssueInfoForPrompt.CreateAsync(c.Results[0].Issue, GitHubDb, ct, contextLimitForIssueBody: 4000, contextLimitForCommentBody: 2000))
                .ToArrayAsync(cancellationToken);

            string candidatesJson = JsonSerializer.Serialize(candidateInfos, IssueInfoForPrompt.JsonOptions);

            IChatClient chatClient = OpenAI.GetChat(Model.Name, secondary: true);

            string prompt =
                $"""
                {TriageSynthesisPrompt}

                NEW ISSUE:
                ```json
                {issueJson}
                ```

                RELATED ISSUES FOUND VIA SEARCH:
                ```json
                {candidatesJson}
                ```
                """;

            string markdownResponse = "";

            await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(prompt, ReasoningChatOptions, cancellationToken: cancellationToken))
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

        private sealed record SearchQueries(string[] Queries);

        private sealed record CandidateClassification(double? Certainty, string Summary);

        public async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectDuplicateIssuesAsync(CancellationToken cancellationToken, IssueInfo[] issuesToClassify = null)
        {
            Logger.DebugLog($"Starting duplicate detection for {Issue.HtmlUrl} with model {Model.Name} for {GitHubUserLogin}");

            if (issuesToClassify is null || issuesToClassify.Length == 0)
            {
                // Step 1: Extract search queries from the new issue
                string[] searchQueries = await ExtractSearchQueriesAsync(cancellationToken);

                OnToolLog($"Extracted {searchQueries.Length} search queries: {string.Join(", ", searchQueries)}");

                if (searchQueries.Length == 0)
                {
                    return [];
                }

                // Step 2: Semantic search for candidate issues
                IssueResultGroup[] candidates = await SearchForRelatedIssuesAsync(searchQueries, cancellationToken);

                OnToolLog($"Found {candidates.Length} candidate issues to classify");

                if (candidates.Length == 0)
                {
                    return [];
                }

                issuesToClassify = [.. candidates.Select(c => c.Results[0].Issue)];
            }

            // Step 3: Classify each candidate independently
            string issueJson = (await IssueInfoForPrompt.CreateAsync(Issue, GitHubDb, cancellationToken, contextLimitForIssueBody: 4000)).AsJson();

            var classificationTasks = issuesToClassify.Select(async candidateIssue =>
            {
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

        private async Task<IssueResultGroup[]> SearchForRelatedIssuesAsync(string[] searchQueries, CancellationToken cancellationToken, int maxCandidates = 15)
        {
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

            return searchResults.Results
                .Where(r => r.Score >= 0.3 && r.Results[0].Issue.Id != Issue.Id)
                .OrderByDescending(r => r.Score)
                .Take(maxCandidates)
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

            ChatResponse<SearchQueries> response = await chatClient.GetResponseAsync<SearchQueries>(prompt, ReasoningChatOptions, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken);

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

            ChatResponse<CandidateClassification> response = await chatClient.GetResponseAsync<CandidateClassification>(prompt, ReasoningChatOptions, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken);

            return response.Result ?? new CandidateClassification(0, string.Empty);
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

        public async Task<IssueInfoForPrompt[]> SearchDotnetGitHubAsync(string[] searchTerms, string extraSearchContext, IssueSearchFilters filters, CancellationToken cancellationToken)
        {
            OnToolLog($"[Tool] Searching for {string.Join(", ", searchTerms)} ({filters})");

            Stopwatch stopwatch = Stopwatch.StartNew();

            int maxResultsPerTerm = MaxResultsPerTerm;
            int maxTotalResults = Math.Min(searchTerms.Length * maxResultsPerTerm, SearchMaxTotalResults);

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
