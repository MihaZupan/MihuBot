using System.Buffers;
using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Qdrant.Client;

#nullable enable

namespace MihuBot.RuntimeUtils.Search;

public sealed class GitHubSearchService
{
    private static readonly SearchValues<char> s_fullTextContainsQueryChars = SearchValues.Create(
        Enumerable.Range(32, 127 - 32).Select(c => (char)c).Where(c => c is not ('"' or '\'' or '\\')).ToArray());

    private readonly ILogger<GitHubSearchService> _logger;
    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly GitHubDataIngestionService _ingestionService;
    private readonly OpenAIService _openAi;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator2;
    private readonly VectorStore _vectorStore;
    private readonly HybridCache _cache;
    internal readonly IConfigurationService _configuration;
    private readonly ServiceConfiguration _serviceConfiguration;

    public Tokenizer Tokenizer => GitHubSemanticSearchIngestionService.Tokenizer;

    private string SearchCollectionName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.SearchCollection", out string name) ? name : "MihuBotGhSearch";

    private string ClassifierModelName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.ClassifierModel", out string name) ? name : "gpt-4.1";
    private string FastClassifierModelName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.FastClassifierModel", out string name) ? name : "gpt-4.1-mini";
    private bool ClassifierModelSecondary => _configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.ClassifierModelSecondary", true);

    private double VectorSearchScoreMultiplier => _configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.VectorScoreMultiplier", 1.0);
    private double FullTextSearchScoreMultiplier => _configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.FTSScoreMultiplier", 0.9);

    internal float DefaultTemperature => _configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.{nameof(DefaultTemperature)}", 0.2f);

    [ImmutableObject(true)]
    private sealed record RawSearchResult(double Score, long RepositoryId, string IssueId, string SubIdentifier);

    public GitHubSearchService(ILogger<GitHubSearchService> logger, IDbContextFactory<GitHubDbContext> db, GitHubDataIngestionService ingestionService, OpenAIService openAi, HybridCache cache, IConfigurationService configuration, ServiceConfiguration serviceConfiguration, VectorStore vectorStore)
    {
        _logger = logger;
        _db = db;
        _ingestionService = ingestionService;
        _openAi = openAi;
        _embeddingGenerator = openAi.GetEmbeddingGenerator(GitHubDbContext.Defaults.EmbeddingModel);
        _embeddingGenerator2 = openAi.GetEmbeddingGenerator(GitHubDbContext.Defaults.EmbeddingModel, secondary: true);
        _vectorStore = vectorStore;
        _cache = cache;
        _configuration = configuration;
        _serviceConfiguration = serviceConfiguration;

        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    var filters = new IssueSearchFilters();
                    var options = new IssueSearchResponseOptions { IncludeIssueComments = true };
                    await SearchIssuesAndCommentsAsync($"Keep-alive query {Environment.TickCount64}", filters, options, cts.Token);
                }
                catch { }
            }
        }, CancellationToken.None);
    }

    public async Task<GitHubSearchResponse> SearchIssuesAndCommentsAsync(
        IList<string> searchTerms,
        IssueSearchBulkFilters bulkFilters,
        IssueSearchFilters filters,
        IssueSearchResponseOptions options,
        CancellationToken cancellationToken)
    {
        using var activity = MihuBotActivitySource.Instance.StartActivity("BulkSearchIssuesAndComments");
        activity?.SetTag("search.terms", searchTerms);
        activity?.SetIssueSearchContext(bulkFilters);
        activity?.SetIssueSearchContext(filters);
        activity?.SetIssueSearchContext(options);
        activity?.SetOperation("search", "bulkSearch");

        ArgumentNullException.ThrowIfNull(searchTerms);
        foreach (string term in searchTerms)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(term);
        }

        if (searchTerms.Count == 0 || (!filters.IncludeOpen && !filters.IncludeClosed) || (!filters.IncludeIssues && !filters.IncludePullRequests))
        {
            _logger.LogInformation("Nothing to search for, returning empty results.");

            return GitHubSearchResponse.Empty;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        var excludeIssueIds = new HashSet<string>(bulkFilters.ExcludeIssues?.Select(i => i.Id) ?? []);

        filters = filters with
        {
            PostFilter = result =>
                (result.Comment is null || !SemanticMarkdownChunker.IsUnlikelyToBeUseful(result.Issue, result.Comment)) &&
                !excludeIssueIds.Contains(result.Issue.Id)
        };

        List<GitHubSearchResponse> searchResponses = new();
        await Parallel.ForEachAsync(searchTerms, async (term, _) =>
        {
            var response = await InnerSearchIssuesAndCommentsAsync(term, bulkFilters, filters, options, cancellationToken);

            lock (searchResponses)
            {
                searchResponses.AddRange(response);
            }
        });

        var combinedGroups = searchResponses
            .SelectMany(r => r.Results)
            .GroupBy(r => r.Issue.Id)
            .Select(group => new IssueResultGroup
            {
                Results = group
                    .SelectMany(r => r.Results)
                    .DistinctBy(e => e.Comment?.Id)
                    .OrderBy(e => e.Comment?.CreatedAt ?? e.Issue.CreatedAt)
                    .ToArray(),
                Score = EstimateCombinedScores(group.Select(r => r.Score).ToArray())
            })
            .Where(g => g.Score >= filters.MinScore)
            .Where(r => excludeIssueIds.Count == 0 || !excludeIssueIds.Contains(r.Issue.Id))
            .OrderByDescending(g => g.Score)
            .Take(options.MaxResults)
            .ToArray();

        var combinedTimings = new SearchTimings();
        foreach (var response in searchResponses)
        {
            combinedTimings.EmbeddingGeneration += response.Timings.EmbeddingGeneration;
            combinedTimings.VectorSearch += response.Timings.VectorSearch;
            combinedTimings.FullTextSearch += response.Timings.FullTextSearch;
            combinedTimings.Database += response.Timings.Database;
            combinedTimings.Reclassification += response.Timings.Reclassification;
        }

        activity?.AddEvent(new ActivityEvent("SearchCompleted", tags: new ActivityTagsCollection
        {
            ["results.count"] = combinedGroups.Length
        }));

        activity?.SetIssueSearchTimings(combinedTimings);

        return new GitHubSearchResponse { Results = combinedGroups, Timings = combinedTimings };
    }

    private async Task<GitHubSearchResponse> InnerSearchIssuesAndCommentsAsync(
        string term,
        IssueSearchBulkFilters bulkFilters,
        IssueSearchFilters filters,
        IssueSearchResponseOptions options,
        CancellationToken cancellationToken)
    {
        using var activity = MihuBotActivitySource.Instance.StartActivity("BulkInnerSearchIssuesAndComments");
        activity?.SetTag("search.term", term);

        IssueSearchResponseOptions vectorSearchOptions = options with
        {
            MaxResults = bulkFilters.MaxResultsPerTerm * (bulkFilters.PostProcessIssues ? 2 : 1)
        };

        var response = await SearchIssuesAndCommentsAsync(term, filters, vectorSearchOptions, cancellationToken);

        activity?.AddEvent(new ActivityEvent("SearchCompleted", tags: new ActivityTagsCollection
        {
            ["results.count"] = response.Results.Count
        }));

        if (!bulkFilters.PostProcessIssues)
        {
            activity?.SetSuccess();

            return response;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            var searchContext = bulkFilters.GetPostProcessingContext(term);

            var results = await FilterOutUnrelatedResultsAsync(searchContext, response.Results, options.PreferSpeed, cancellationToken);

            response = response with
            {
                Timings = response.Timings with { Reclassification = stopwatch.Elapsed },
                Results = [.. results.Take(bulkFilters.MaxResultsPerTerm)]
            };

            activity?.AddEvent(new ActivityEvent("PostProcessingCompleted", tags: new ActivityTagsCollection
            {
                ["results.count"] = response.Results.Count,
            }));

            activity?.SetIssueSearchTimings(response.Timings);

            activity?.SetSuccess();
        }
        catch (Exception ex)
        {
            response = response with
            {
                Timings = response.Timings with { Reclassification = stopwatch.Elapsed },
                Results = [.. response.Results.Take(bulkFilters.MaxResultsPerTerm)]
            };

            activity?.SetIssueSearchTimings(response.Timings);

            activity?.SetError(ex);

            _logger.LogDebug(ex, "Triage: Error filtering unrelated results.");
        }

        return response;
    }

    public async Task<GitHubSearchResponse> SearchIssuesAndCommentsAsync(
        string query,
        IssueSearchFilters filters,
        IssueSearchResponseOptions options,
        CancellationToken cancellationToken)
    {
        using var activity = MihuBotActivitySource.Instance.StartActivity("SearchIssuesAndComments");
        activity?.SetTag("search.query", query);
        activity?.SetIssueSearchContext(filters);
        activity?.SetIssueSearchContext(options);
        activity?.SetOperation("search", "search");

        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxResults);

        if (!filters.IncludeOpen && !filters.IncludeClosed)
        {
            throw new ArgumentException($"At least one of {nameof(filters.IncludeOpen)} or {nameof(filters.IncludeClosed)} must be true.");
        }

        if (!filters.IncludeIssues && !filters.IncludePullRequests)
        {
            throw new ArgumentException($"At least one of {nameof(filters.IncludeIssues)} or {nameof(filters.IncludePullRequests)} must be true.");
        }

        var timings = new SearchTimings();

        int topVectors = 200;

        topVectors = Math.Min(topVectors, options.MaxResults * 10);

        long repositoryFilter = await _ingestionService.TryGetKnownRepositoryIdAsync(filters.Repository, cancellationToken);

        if (repositoryFilter <= 0 && !string.IsNullOrEmpty(filters.Repository))
        {
            activity?.SetError($"Repository '{filters.Repository}' not found,");
            _logger.LogDebug("Repository '{Repository}' not found, skipping search.", filters.Repository);
            return new GitHubSearchResponse { Results = [], Timings = timings };
        }

        activity?.SetTag("repository.id", repositoryFilter);

        query = query.Trim();

        _logger.LogDebug("Starting search for '{Query}'", query);

        activity?.AddEvent(new ActivityEvent("StartingParallelSearches"));

        Task<RawSearchResult[]> fullTextSearchTask = Task.Run(() => FullTextSearchAsync(query, Math.Min(100, topVectors), repositoryFilter, timings, cancellationToken), cancellationToken);

        RawSearchResult[] results = await VectorSearchAsync(query, topVectors, repositoryFilter, timings, cancellationToken);

        // Combine duplicate issue/comment results (e.g. multiple sections of the same comment)
        results = results
            .Concat(await fullTextSearchTask)
            .GroupBy(r => (r.IssueId, r.SubIdentifier))
            .Select(g => g.First() with { Score = EstimateCombinedScores([.. g.Select(e => e.Score)]) })
            .ToArray();

        activity?.AddEvent(new ActivityEvent("SearchResultsCombined", DateTimeOffset.UtcNow, new ActivityTagsCollection
        {
            ["rawResultsCount"] = results.Length
        }));

        if (results.Length == 0)
        {
            activity?.SetTag("results.count", 0);
            activity?.SetSuccess();

            return new GitHubSearchResponse { Results = [], Timings = timings };
        }

        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        Stopwatch databaseQueryStopwatch = Stopwatch.StartNew();

        string[] issueIds = [.. results.Select(r => r.IssueId).Distinct()];
        string[] commentIds = [.. results.Where(r => !string.IsNullOrEmpty(r.SubIdentifier)).Select(r => r.SubIdentifier).Distinct()];

        IQueryable<IssueInfo> issuesQuery = db.Issues
            .AsNoTracking()
            .Where(i => issueIds.Contains(i.Id));

        if (filters.CreatedAfter.HasValue)
        {
            issuesQuery = issuesQuery.Where(i => i.CreatedAt >= filters.CreatedAfter.Value);
        }

        if (!filters.IncludeOpen)
        {
            issuesQuery = issuesQuery.Where(i => i.ClosedAt != null);
        }
        else if (!filters.IncludeClosed)
        {
            issuesQuery = issuesQuery.Where(i => i.ClosedAt == null);
        }

        if (!filters.IncludeIssues)
        {
            issuesQuery = issuesQuery.Where(i => i.IssueType != IssueType.Issue);
        }

        if (!filters.IncludePullRequests)
        {
            issuesQuery = issuesQuery.Where(i => i.IssueType != IssueType.PullRequest);
        }

        if (!string.IsNullOrEmpty(filters.LabelContains))
        {
            issuesQuery = issuesQuery.Where(i => i.Labels.Any(l => l.Name.Contains(filters.LabelContains)));
        }

        issuesQuery = issuesQuery
            .Include(i => i.User)
            .Include(i => i.Labels)
            .Include(i => i.Repository)
            .Include(i => i.PullRequest);

        if (options.IncludeIssueComments)
        {
            issuesQuery = issuesQuery
                .Include(i => i.Comments)
                    .ThenInclude(c => c.User);
        }

        List<IssueInfo> issues = await issuesQuery
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        List<CommentInfo> comments = await db.Comments
            .AsNoTracking()
            .Where(i => commentIds.Contains(i.Id))
            .Include(i => i.User)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        timings.Database = databaseQueryStopwatch.Elapsed;

        _logger.LogDebug("Search for '{Query}' returned {IssuesCount} unique issues, {CommentCount} comments. {Timings}", query, issues.Count, comments.Count, timings);

        Dictionary<string, IssueInfo> issuesById = issues.ToDictionary(i => i.Id, i => i);
        Dictionary<string, CommentInfo> commentsById = comments.ToDictionary(c => c.Id, c => c);

        IssueResultGroup[] issueGroups = results
            .Select(r =>
            {
                _ = issuesById.TryGetValue(r.IssueId, out IssueInfo? issue);
                CommentInfo? comment = r.SubIdentifier is null ? null : (commentsById.TryGetValue(r.SubIdentifier, out CommentInfo? c) ? c : null);
                return new IssueSearchResult { Score = r.Score, Issue = issue!, Comment = comment };
            })
            .Where(r => r.Issue is not null)
            .Where(r => filters.PostFilter is null || filters.PostFilter(r))
            .GroupBy(r => r.Issue.Id)
            .Select(g => g.ToArray())
            .Select(matches => new IssueResultGroup { Results = matches, Score = EstimateCombinedScores([.. matches.Select(m => m.Score)]) })
            .Where(r => r.Score >= filters.MinScore)
            .OrderByDescending(r => r.Score)
            .Take(options.MaxResults)
            .ToArray();

        activity?.SetTag("results.count", issueGroups.Length);
        activity?.SetTag("timings.embeddingGeneration", timings.EmbeddingGeneration.TotalMilliseconds);
        activity?.SetTag("timings.vectorSearch", timings.VectorSearch.TotalMilliseconds);
        activity?.SetTag("timings.fullTextSearch", timings.FullTextSearch.TotalMilliseconds);
        activity?.SetTag("timings.database", timings.Database.TotalMilliseconds);

        activity?.SetSuccess();

        return new GitHubSearchResponse { Results = issueGroups, Timings = timings };
    }

    private static double EstimateCombinedScores(double[] scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentOutOfRangeException.ThrowIfZero(scores.Length);

        double max = scores.Max();

        // Boost issues with multiple potentially related comments.
        double threshold = Math.Max(max * 0.75, 0.35);
        int commentsOverThreshold = scores.Count(s => s >= threshold);

        return BoostScore(max, Math.Pow(1.01, Math.Min(commentsOverThreshold, 10)));

        static double BoostScore(double score, double factor)
        {
            double offset = 1 - score;
            offset *= 1 / factor;
            return Math.Clamp(1 - offset, 0, 1);
        }
    }

    private async Task<RawSearchResult[]> VectorSearchAsync(string query, int topVectors, long repositoryFilter, SearchTimings timings, CancellationToken cancellationToken)
    {
        if (_serviceConfiguration.DisableVectorSearch)
        {
            _logger.LogDebug("Vector search is disabled, skipping search for '{Query}'", query);
            return [];
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        RawSearchResult[] results = await _cache.GetOrCreateAsync($"{nameof(GitHubSearchService)}/{nameof(VectorSearchAsync)}/{$"{repositoryFilter}/{topVectors}/{query}".GetUtf8Sha3_512HashBase64Url()}", async cancellationToken =>
        {
            IEmbeddingGenerator<string, Embedding<float>> generator = ClassifierModelSecondary ? _embeddingGenerator2 : _embeddingGenerator;

            ReadOnlyMemory<float> queryEmbedding = await _embeddingGenerator.GenerateVectorAsync(query, cancellationToken: CancellationToken.None);
            timings.EmbeddingGeneration = stopwatch.Elapsed;

            VectorStoreCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(SearchCollectionName);

            var results = new List<RawSearchResult>();

            var options = new VectorSearchOptions<SemanticSearchRecord>
            {
                Filter = repositoryFilter > 0 ? record => record.RepositoryId == repositoryFilter : null,
                IncludeVectors = false,
            };

            double scoreMultiplier = VectorSearchScoreMultiplier;

            await foreach (VectorSearchResult<SemanticSearchRecord> item in vectorCollection.SearchAsync(queryEmbedding, topVectors, options, cancellationToken: CancellationToken.None))
            {
                if (item.Score.HasValue && item.Score > 0.15)
                {
                    results.Add(new RawSearchResult(scoreMultiplier * item.Score.Value, item.Record.RepositoryId, item.Record.IssueId, item.Record.SubIdentifier));
                }
            }

            return results.ToArray();
        }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1) }, [nameof(GitHubSearchService)], cancellationToken);

        timings.VectorSearch = stopwatch.Elapsed - timings.EmbeddingGeneration;

        return results;
    }

    private async Task<RawSearchResult[]> FullTextSearchAsync(string query, int count, long repositoryFilter, SearchTimings timings, CancellationToken cancellationToken)
    {
        if (_serviceConfiguration.DisableFullTextSearch)
        {
            _logger.LogDebug("Full-text search is disabled, skipping search for '{Query}'", query);
            return [];
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        query = query.Replace('\t', ' ');

        if (query.ContainsAnyExcept(s_fullTextContainsQueryChars))
        {
            query = string.Concat(query.Where(s_fullTextContainsQueryChars.Contains));
        }

        query = query.Trim();

        if (query.Length < 3)
        {
            return [];
        }

        RawSearchResult[] results = await _cache.GetOrCreateAsync($"{nameof(GitHubSearchService)}/{nameof(FullTextSearchAsync)}/{$"{repositoryFilter}/{count}/{query}".GetUtf8Sha3_512HashBase64Url()}", async cancellationToken =>
        {
            await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

            IQueryable<TextEntry> dbQuery = db.TextEntries.AsNoTracking();

            if (repositoryFilter > 0)
            {
                dbQuery = dbQuery.Where(e => e.RepositoryId == repositoryFilter);
            }

            var textResults = await dbQuery
                .Where(e => e.TextVector.Matches(EF.Functions.PhraseToTsQuery("english", query)))
                .Select(e => new
                {
                    e.RepositoryId,
                    e.IssueId,
                    e.SubIdentifier,
                    Rank = e.TextVector.Rank(EF.Functions.PhraseToTsQuery("english", query))
                })
                .OrderByDescending(e => e.Rank)
                .Where(e => e.Rank > 0.60)
                .Take(count)
                .ToArrayAsync(cancellationToken);

            double scoreMultiplier = FullTextSearchScoreMultiplier;

            return textResults
                .Select(r => new RawSearchResult(scoreMultiplier * r.Rank, r.RepositoryId, r.IssueId, r.SubIdentifier))
                .ToArray();
        }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1) }, [nameof(GitHubSearchService)], cancellationToken);

        timings.FullTextSearch = stopwatch.Elapsed;

        return results;
    }

    public async Task<IList<IssueResultGroup>> FilterOutUnrelatedResultsAsync(
        string searchContext,
        IList<IssueResultGroup> results,
        bool preferSpeed,
        CancellationToken cancellationToken)
    {
        using var activity = MihuBotActivitySource.Instance.StartActivity("FilterOutUnrelatedResults");
        activity?.SetTag("search.context", searchContext);
        activity?.SetTag("search.results.count", results.Count);
        activity?.SetOperation("search", "filter");

        ArgumentException.ThrowIfNullOrWhiteSpace(searchContext);
        ArgumentNullException.ThrowIfNull(results);

        searchContext = searchContext.Trim();

        if (results.Count == 0)
        {
            activity?.SetTag("results.count", 0);
            activity?.SetSuccess();

            return results;
        }

        IChatClient fastClassifierChat = _openAi.GetChat(preferSpeed ? FastClassifierModelName : ClassifierModelName, ClassifierModelSecondary);

        int maxIssueCount = 50;
        int bodyContextWindow = 80;
        int maxComments = 6;

        IssueRelevance[] relevances = await GetRelevancesAsync(maxIssueCount, bodyContextWindow, maxComments);

        activity?.AddEvent(new ActivityEvent("RelevanceClassificationCompleted", tags: new ActivityTagsCollection
        {
            ["results.count"] = results.Count,
            ["relevances.count"] = relevances.Length
        }));

        if (relevances.Length < 3 && results.Count >= 20)
        {
            int relevanceMultiplier = 2;

            activity?.AddEvent(new ActivityEvent("RelevanceClassificationInsufficientResults", tags: new ActivityTagsCollection
            {
                ["results.count"] = results.Count,
                ["relevances.count"] = relevances.Length,
                ["multiplier"] = relevanceMultiplier
            }));

            maxIssueCount *= relevanceMultiplier;
            bodyContextWindow *= relevanceMultiplier;
            maxComments *= relevanceMultiplier;

            relevances = await GetRelevancesAsync(maxIssueCount, bodyContextWindow, maxComments);

            activity?.AddEvent(new ActivityEvent("RelevanceClassificationRetryCompleted", tags: new ActivityTagsCollection
            {
                ["results.count"] = results.Count,
                ["relevances.count"] = relevances.Length
            }));

            if (relevances.Length < 3)
            {
                relevances = [];
            }
        }

        IssueResultGroup[] newResults = relevances
            .Where(r => (uint)(r.IssueNumber - 1) < (uint)results.Count)
            .Select(s => results[s.IssueNumber - 1] with { Score = Math.Min(1, s.Score!.Value) })
            .DistinctBy(r => r.Issue.Id)
            .OrderByDescending(r => r.Score)
            .ToArray();

        activity?.SetTag("results.new.count", newResults.Length);

        results = newResults.Length > 0
            ? newResults
            : results;

        activity?.SetTag("results.count", results.Count);
        activity?.SetSuccess();

        return results;

        async Task<IssueRelevance[]> GetRelevancesAsync(int issueCount, int bodyContext, int maxComments)
        {
            string prompt = GeneratePrompt(issueCount, bodyContext, maxComments);

            return await _cache.GetOrCreateAsync(
                $"{nameof(GitHubSearchService)}/{nameof(FilterOutUnrelatedResultsAsync)}/{prompt.GetUtf8Sha3_512HashBase64Url()}",
                async cancellationToken =>
                {
                    const int Retries = 3;

                    Stopwatch localStopwatch = Stopwatch.StartNew();

                    ChatResponse<IssueRelevance[]>? relevances = null;
                    for (int i = 0; i < Retries; i++)
                    {
                        try
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            cts.CancelAfter(TimeSpan.FromMinutes(1));

                            var options = new ChatOptions
                            {
                                Temperature = DefaultTemperature
                            };

                            relevances = await fastClassifierChat.GetResponseAsync<IssueRelevance[]>(prompt, options, useJsonSchemaResponseFormat: true, cancellationToken: cts.Token);
                            break;
                        }
                        catch (Exception ex) when (i < Retries - 1 && !cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogError(ex, "Relevance classification for failed (attempt {Count})", i + 1);
                        }
                    }

                    _logger.LogDebug("Relevance classification took {ElapsedMs} ms for {IssueCount} issues ({ClassifierModel}, len={PromptLength})",
                        (int)localStopwatch.ElapsedMilliseconds, Math.Min(issueCount, results.Count), FastClassifierModelName, prompt.Length);

                    return relevances!.Result.Where(s => s.Score > 0.05).ToArray();
                }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1) }, [nameof(GitHubSearchService)], cancellationToken);
        }

        string GeneratePrompt(int issueCount, int bodyContext, int maxComments)
        {
            return
                $"""
                You are part of an automated system that looks for GitHub issues that are relevant to a user's task/query.

                {searchContext}

                Classify the relevance of the following GitHub issues and comments based on this context.
                Specify the approximate relevance score from 0 to 1 for each issue, where 0 means not relevant at all and 1 means very relevant.
                Return the set of issue numbers with their relevance scores.

                The issues are:

                {string.Join("\n\n\n---\n\n\n", results.Take(issueCount).Select((r, index) => GetShortIssueDescription(r.Results, index + 1, bodyContext, maxComments)))}
                """;
        }

        string GetShortIssueDescription(IList<IssueSearchResult> results, int position, int bodyContext, int maxComments)
        {
            IssueInfo issue = results[0].Issue;

            StringBuilder sb = new();

            sb.AppendLine($"{issue.IssueType.ToDisplayString()} {position} in {issue.Repository.FullName} by {issue.User.Login}: {issue.Title}");

            string closedInfo =
                !issue.ClosedAt.HasValue ? "still open" :
                issue.PullRequest is { } pr ? pr.MergedAt.HasValue
                    ? $"merged {pr.MergedAt.Value.ToISODate()}"
                    : $"closed {issue.ClosedAt.Value.ToISODate()} without merging" :
                $"closed {issue.ClosedAt.Value.ToISODate()}";

            sb.AppendLine($"Opened {issue.CreatedAt.ToISODate()}, {closedInfo}.");

            if (issue.Labels.FirstOrDefault(l => l.Name.StartsWith("area-", StringComparison.OrdinalIgnoreCase))?.Name is { } areaLabel)
            {
                sb.AppendLine($"Area: {areaLabel.AsSpan(5)}");
            }

            string body = TrimBody(issue.Body, bodyContext);

            if (!string.IsNullOrEmpty(body))
            {
                sb.AppendLine($"Body: {body}");
            }

            foreach (CommentInfo comment in results
                .Where(r => r.Score >= 0.20 && r.Comment is not null)
                .OrderByDescending(r => r.Score)
                .Select(r => r.Comment!)
                .Where(c => !SemanticMarkdownChunker.IsUnlikelyToBeUseful(issue, c))
                .Take(maxComments)
                .OrderBy(c => c.CreatedAt))
            {
                sb.AppendLine($"Comment by {comment.User.Login}: {TrimBody(comment.Body, bodyContext)}");
                sb.AppendLine();
            }

            return sb.ToString();

            string TrimBody(string body, int bodyContext)
            {
                if (string.IsNullOrEmpty(body))
                {
                    return string.Empty;
                }

                body = SemanticMarkdownChunker.TrimTextToTokens(Tokenizer, body, bodyContext);

                while (body.Contains("\n\n\n", StringComparison.Ordinal))
                    body = body.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);

                while (body.Contains("--", StringComparison.Ordinal))
                    body = body.Replace("--", "-", StringComparison.Ordinal);

                return body.Trim();
            }
        }
    }

    [ImmutableObject(true)]
    private sealed record IssueRelevance(int IssueNumber, double? Score);
}
