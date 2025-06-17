using System.ClientModel;
using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MihuBot.RuntimeUtils;

public sealed class GitHubSearchService : IHostedService
{
    private const string EmbeddingModel = "text-embedding-3-small";
    private const int EmbeddingDimensions = 1536;
    private const int SmallSectionTokenThreshold = 200;

    public Tokenizer Tokenizer { get; }

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;
    private readonly OpenAIService _openAI;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator2;
    private readonly QdrantClient _qdrantClient;
    private readonly VectorStore _vectorStore;
    private readonly HybridCache _cache;
    private readonly GitHubDataService _dataService;
    private readonly CancellationTokenSource _updateCts = new();
    private Task _updatesTask;

    private string SearchCollectionName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.SearchCollection", out string name) ? name : nameof(GitHubSearchService);
    private string UpdateCollectionName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.UpdateCollection", out string name) ? name : nameof(GitHubSearchService);

    private string ClassifierModelName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.ClassifierModel", out string name) ? name : "gpt-4.1-mini";
    private string FastClassifierModelName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.FastClassifierModel", out string name) ? name : "gpt-4.1-nano";
    private bool ClassifierModelSecondary => _configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.ClassifierModelSecondary", true);

    public GitHubSearchService(IDbContextFactory<GitHubDbContext> db, Logger logger, OpenAIService openAi, VectorStore vectorStore, QdrantClient qdrantClient, IConfigurationService configuration, HybridCache cache, GitHubDataService dataService)
    {
        _db = db;
        _logger = logger;
        _openAI = openAi;
        _vectorStore = vectorStore;
        _qdrantClient = qdrantClient;
        _configuration = configuration;
        _cache = cache;
        _dataService = dataService;
        _embeddingGenerator = openAi.GetEmbeddingGenerator(EmbeddingModel);
        _embeddingGenerator2 = openAi.GetEmbeddingGenerator(EmbeddingModel, secondary: true);
        Tokenizer = TiktokenTokenizer.CreateForModel(EmbeddingModel);
    }

    public sealed record IssueSearchResult(double Score, IssueInfo Issue, CommentInfo Comment);

    public sealed record IssueSearchFilters(bool IncludeOpen, bool IncludeClosed, bool IncludeIssues, bool IncludePullRequests, DateTime? CreatedAfter = null)
    {
        public string Repository { get; set; }

        public override string ToString()
        {
            string s = $"{nameof(IncludeOpen)}={IncludeOpen}, {nameof(IncludeClosed)}={IncludeClosed}, {nameof(IncludeIssues)}={IncludeIssues}, {nameof(IncludePullRequests)}={IncludePullRequests}";

            if (CreatedAfter.HasValue)
            {
                s += $", {nameof(CreatedAfter)}={CreatedAfter.Value.ToISODate()}";
            }

            if (!string.IsNullOrEmpty(Repository))
            {
                s += $", {nameof(Repository)}={Repository}";
            }

            return s;
        }
    }

    [ImmutableObject(true)]
    private sealed class RawSearchResult(double score, long repositoryId, string issueId, string subIdentifier)
    {
        public double Score { get; } = score;
        public long RepositoryId { get; } = repositoryId;
        public string IssueId { get; } = issueId;
        public string SubIdentifier { get; } = subIdentifier;
    }

    private async Task<(RawSearchResult[] Results, TimeSpan EmbeddingTime)> SearchAsyncCore(string query, int topVectors, long repositoryFilter, CancellationToken cancellationToken)
    {
        query = query.Trim();

        TimeSpan embeddingTime = TimeSpan.Zero;

        // Intentionally ignoring the cancellation token on the cache query so that we still get the results in the background.
        RawSearchResult[] results = await _cache.GetOrCreateAsync($"/embeddingsearch/{topVectors}/{repositoryFilter}/{query.GetUtf8Sha384HashBase64Url()}", async _ =>
        {
            Stopwatch embeddingStopwatch = Stopwatch.StartNew();
            ReadOnlyMemory<float> queryEmbedding = await _embeddingGenerator.GenerateVectorAsync(query, cancellationToken: CancellationToken.None);
            embeddingTime = embeddingStopwatch.Elapsed;

            VectorStoreCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(SearchCollectionName);

            var results = new List<RawSearchResult>();

            var options = new VectorSearchOptions<SemanticSearchRecord>
            {
                Filter = repositoryFilter > 0 ? record => record.RepositoryId == repositoryFilter : null,
                IncludeVectors = false,
            };

            await foreach (VectorSearchResult<SemanticSearchRecord> item in vectorCollection.SearchAsync(queryEmbedding, topVectors, options, cancellationToken: CancellationToken.None))
            {
                if (item.Score.HasValue && item.Score > 0.15)
                {
                    results.Add(new RawSearchResult(item.Score.Value, item.Record.RepositoryId, item.Record.IssueId, item.Record.SubIdentifier));
                }
            }

            return results.ToArray();
        }, cancellationToken: CancellationToken.None).WaitAsyncAndSupressNotObserved(cancellationToken);

        return (results, embeddingTime);
    }

    public async Task<IssueSearchResult[]> SearchIssuesAndCommentsAsync(string query, int maxResults, IssueSearchFilters filters, bool includeAllIssueComments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        if (!filters.IncludeOpen && !filters.IncludeClosed)
        {
            throw new ArgumentException($"At least one of {nameof(filters.IncludeOpen)} or {nameof(filters.IncludeClosed)} must be true.");
        }

        if (!filters.IncludeIssues && !filters.IncludePullRequests)
        {
            throw new ArgumentException($"At least one of {nameof(filters.IncludeIssues)} or {nameof(filters.IncludePullRequests)} must be true.");
        }

        await using GitHubDbContext db = _db.CreateDbContext();

        if (!_configuration.TryGet(null, $"{nameof(GitHubSearchService)}.TopVectors", out string topVectorsStr) ||
            !int.TryParse(topVectorsStr, out int topVectors) ||
            topVectors is < 1 or > 10_000)
        {
            topVectors = 350;
        }

        topVectors = Math.Min(topVectors, maxResults * 10);

        long repositoryFilter = await _dataService.TryGetKnownRepositoryIdAsync(filters.Repository, cancellationToken);

        if (repositoryFilter <= 0 && !string.IsNullOrEmpty(filters.Repository))
        {
            _logger.DebugLog($"Repository '{filters.Repository}' not found, skipping search.");
            return [];
        }

        _logger.DebugLog($"Starting search for '{query}'");

        Stopwatch stopwatch = Stopwatch.StartNew();

        (RawSearchResult[] results, TimeSpan embeddingGenTime) = await SearchAsyncCore(query, topVectors, repositoryFilter, cancellationToken);

        TimeSpan embeddingSearchTime = stopwatch.Elapsed;
        stopwatch.Restart();

        if (results.Length == 0)
        {
            return [];
        }

        stopwatch.Restart();

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
            issuesQuery = issuesQuery.Where(i => i.State != Octokit.ItemState.Open);
        }
        else if (!filters.IncludeClosed)
        {
            issuesQuery = issuesQuery.Where(i => i.State != Octokit.ItemState.Closed);
        }

        if (!filters.IncludeIssues)
        {
            issuesQuery = issuesQuery.Where(i => i.PullRequest != null);
        }
        else if (!filters.IncludePullRequests)
        {
            issuesQuery = issuesQuery.Where(i => i.PullRequest == null);
        }

        issuesQuery = issuesQuery
            .Include(i => i.User)
            .Include(i => i.Labels)
            .Include(i => i.Repository)
            .Include(i => i.PullRequest);

        if (includeAllIssueComments)
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

        TimeSpan databaseQueryTime = stopwatch.Elapsed;

        _logger.DebugLog($"Search for '{query}' returned {issues.Count} unique issues, {comments.Count} comments." +
            $" Embedding={embeddingGenTime.TotalMilliseconds:F2} Search={(embeddingSearchTime - embeddingGenTime).TotalMilliseconds:F2} Database={databaseQueryTime.TotalMilliseconds:F2}");

        Dictionary<string, IssueInfo> issuesById = issues.ToDictionary(i => i.Id, i => i);
        Dictionary<string, CommentInfo> commentsById = comments.ToDictionary(c => c.Id, c => c);

        return results
            .Select(r =>
            {
                _ = issuesById.TryGetValue(r.IssueId, out IssueInfo issue);
                CommentInfo comment = r.SubIdentifier is null ? null : (commentsById.TryGetValue(r.SubIdentifier, out CommentInfo c) ? c : null);
                return new IssueSearchResult(r.Score, issue, comment);
            })
            .Where(r => r.Issue is not null)
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .ToArray();
    }

    public static (IssueSearchResult[] Results, double Score)[] GroupResultsByIssue(IssueSearchResult[] results)
    {
        return results
            .GroupBy(r => r.Issue.Id)
            .Select(g => g.ToArray())
            .Select(r => (Results: r, Score: EstimateCombinedScores(r.Select(r => r.Score).ToArray())))
            .OrderByDescending(p => p.Score)
            .ToArray();
    }

    public static double EstimateCombinedScores(double[] scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentOutOfRangeException.ThrowIfZero(scores.Length);

        double max = scores.Max();
        double offset = 1 - max;

        // Boost issues with multiple potentially related comments.
        double threshold = Math.Max(max * 0.75, 0.35);
        int commentsOverThreshold = scores.Count(s => s >= threshold);

        if (commentsOverThreshold > 0)
        {
            offset *= Math.Pow(0.99, Math.Min(commentsOverThreshold, 10));
        }

        return Math.Clamp(1 - offset, 0, 1);
    }

    public async Task<(IssueSearchResult[] Results, double Score)[]> FilterOutUnrelatedResults(
        string searchQuery,
        string extraSearchContext,
        bool preferSpeed,
        (IssueSearchResult[] Results, double Score)[] results,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchQuery);
        ArgumentNullException.ThrowIfNull(results);

        if (results.Length == 0)
        {
            return results;
        }

        searchQuery = searchQuery.Trim();

        string classifierModel = preferSpeed ? FastClassifierModelName : ClassifierModelName;
        IChatClient fastClassifierChat = _openAI.GetChat(classifierModel, ClassifierModelSecondary);

        int contextMultiplier = preferSpeed ? 1 : 2;
        int maxIssueCount = 25 * contextMultiplier;
        int bodyContextWindow = 40 * contextMultiplier;
        int maxComments = 3 * contextMultiplier;

        IssueRelevance[] relevances = await GetRelevancesAsync(maxIssueCount, bodyContextWindow, maxComments);

        if (relevances.Length <= 5 && results.Length >= 20)
        {
            maxIssueCount *= 2;
            bodyContextWindow *= 2;
            maxComments *= 2;

            relevances = await GetRelevancesAsync(maxIssueCount, bodyContextWindow, maxComments);

            if (relevances.Length < 3 && results.Length >= 20)
            {
                relevances = [];
            }
        }

        (IssueSearchResult[] Results, double Score)[] newResults = relevances
            .Where(r => (uint)(r.IssueNumber - 1) < (uint)results.Length)
            .Select(s => results[s.IssueNumber - 1] with { Score = s.Score })
            .DistinctBy(r => r.Results[0].Issue.Id)
            .OrderByDescending(r => r.Score)
            .ToArray();

        return newResults.Length > 0 ? newResults : results;

        async Task<IssueRelevance[]> GetRelevancesAsync(int issueCount, int bodyContext, int maxComments)
        {
            return await _cache.GetOrCreateAsync($"/searchrelevance/{$"{classifierModel}/{results.Length}-{issueCount}-{bodyContext}-{maxComments}/{searchQuery}/{extraSearchContext}".GetUtf8Sha384HashBase64Url()}", async _ =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                string prompt = GeneratePrompt(issueCount, bodyContext, maxComments);

                ChatResponse<IssueRelevance[]> relevances = null;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                        relevances = await fastClassifierChat.GetResponseAsync<IssueRelevance[]>(prompt, useJsonSchemaResponseFormat: true, cancellationToken: cts.Token);
                        break;
                    }
                    catch (Exception ex) when (i < 3)
                    {
                        _logger.DebugLog($"Relevance classification for '{searchQuery}' failed (attempt {i + 1}): {ex}");
                    }
                }

                _logger.DebugLog($"Relevance classification for '{searchQuery}' took {stopwatch.ElapsedMilliseconds:F2} ms for {Math.Min(issueCount, results.Length)} issues");

                return relevances.Result
                    .Where(s => s.Score > 0.05)
                    .ToArray();
            }, cancellationToken: CancellationToken.None).WaitAsyncAndSupressNotObserved(cancellationToken);
        }

        string GeneratePrompt(int issueCount, int bodyContext, int maxComments)
        {
            return
                $"""
                Classify the relevance of the following GitHub issues and comments based on the search query "{searchQuery}"{(string.IsNullOrEmpty(extraSearchContext) ? "" : $" in the context of '{extraSearchContext}'")}.
                Specify the approximate relevance score from 0 to 1 for each issue, where 0 means not relevant and 1 means very relevant.
                If an issue is unlikely to be relevant, set the score to 0.
                Return the set of issue numbers with their relevance scores.

                Prefer faster responses over accuracy.

                The issues are:

                {string.Join("\n\n\n---\n\n\n", results.Take(issueCount).Select((r, index) => GetShortIssueDescription(r.Results, index + 1, bodyContext, maxComments)))}
                """;
        }

        string GetShortIssueDescription(IssueSearchResult[] results, int position, int bodyContext, int maxComments)
        {
            IssueInfo issue = results[0].Issue;

            StringBuilder sb = new();

            sb.AppendLine($"{(issue.PullRequest is null ? "Issue" : "Pull request")} {position} in {issue.Repository.FullName} by {issue.User.Login}: {issue.Title}");

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

            foreach (IssueSearchResult result in results
                .Where(r => r.Score >= 0.20 && r.Comment is not null)
                .OrderByDescending(r => r.Score)
                .Take(maxComments))
            {
                sb.AppendLine($"Comment by {result.Comment.User.Login}: {TrimBody(result.Comment.Body, bodyContext)}");
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
    private sealed class IssueRelevance
    {
        public int IssueNumber { get; set; }
        public double Score { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            using AsyncFlowControl _ = ExecutionContext.SuppressFlow();

            _updatesTask = Task.Run(async () => await RunUpdateLoopAsync(cancellationToken), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _updateCts.CancelAsync();

        if (_updatesTask is not null)
        {
            await _updatesTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _updateCts.Token);
            cancellationToken = linkedCts.Token;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (_configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.PauseIngestion", false))
                    {
                        continue;
                    }

                    (int updates, int tokens) = await UpdateIngestedEmbeddingsAsync(cancellationToken);
                    if (updates > 0)
                    {
                        _logger.TraceLog($"{nameof(GitHubSearchService)}: Performed {updates} DB updates, consumed {tokens} tokens");

                        if (updates < 20)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                        }
                    }

                    if (tokens > 1_000)
                    {
                        const int TokenLimitPerMinute = 950_000;
                        await Task.Delay(TimeSpan.FromMinutes((double)tokens / TokenLimitPerMinute), cancellationToken);
                    }

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    string errorMessage = $"{nameof(GitHubSearchService)}: Update failed ({consecutiveFailureCount}): {ex}";
                    _logger.DebugLog(errorMessage);

                    await Task.Delay(TimeSpan.FromMinutes(5) * consecutiveFailureCount, cancellationToken);

                    if (consecutiveFailureCount == 2)
                    {
                        await _logger.DebugAsync(errorMessage);
                    }
                }
            }
        }
        catch when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected exception: {ex}");
        }
    }

    private async Task<(int DbUpdates, int Tokens)> UpdateIngestedEmbeddingsAsync(CancellationToken cancellationToken)
    {
        const int BatchSize = 100;

        await using GitHubDbContext db = _db.CreateDbContext();

        VectorStoreCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(UpdateCollectionName);

        if (!await vectorCollection.CollectionExistsAsync(cancellationToken))
        {
            await _qdrantClient.CreateCollectionAsync(UpdateCollectionName,
                vectorsConfig: new VectorParams
                {
                    Size = EmbeddingDimensions,
                    Distance = Distance.Cosine,
                    OnDisk = true
                },
                quantizationConfig: new QuantizationConfig
                {
                    Scalar = new ScalarQuantization
                    {
                        Type = QuantizationType.Int8
                    }
                },
                cancellationToken: cancellationToken);
        }

        IQueryable<IssueInfo> issuesQuery = db.Issues
            .AsNoTracking()
            .Where(issue =>
                !db.IngestedEmbeddings.Any(entry => entry.ResourceIdentifier == issue.Id) ||
                db.IngestedEmbeddings.First(entry => entry.ResourceIdentifier == issue.Id).UpdatedAt < issue.UpdatedAt)
            .OrderBy(i => i.UpdatedAt);

        IQueryable<CommentInfo> commentsQuery = db.Comments
            .AsNoTracking()
            .Where(comment =>
                !db.IngestedEmbeddings.Any(entry => entry.ResourceIdentifier == comment.IssueId) ||
                db.IngestedEmbeddings.First(entry => entry.ResourceIdentifier == comment.IssueId).UpdatedAt < comment.UpdatedAt)
            .OrderBy(c => c.UpdatedAt);

        if (_configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.IngestHttpOnly", false))
        {
            issuesQuery = issuesQuery.Where(i => i.Labels.Any(l => l.Name == "area-System.Net.Http"));
            commentsQuery = commentsQuery.Where(c => c.Issue.Labels.Any(l => l.Name == "area-System.Net.Http"));
        }

        List<string> updatedIssues = await issuesQuery
            .Select(i => i.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        List<string> updatedComments = await commentsQuery
            .Select(i => i.IssueId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        HashSet<string> updatedIssueIds = [.. updatedIssues, .. updatedComments];

        if (updatedIssueIds.Count == 0)
        {
            return (0, 0);
        }

        _logger.TraceLog($"{nameof(GitHubSearchService)}: Found {updatedIssueIds.Count} issues to update");

        int updatesPerformed = 0;
        int tokensConumed = 0;

        await Parallel.ForEachAsync(updatedIssueIds, new ParallelOptions { MaxDegreeOfParallelism = 20 }, async (issueId, _) =>
        {
            (int dbUpdates, int tokens) = await UpdateRecordsForIssueAsync(issueId, vectorCollection, cancellationToken);
            Interlocked.Add(ref updatesPerformed, dbUpdates);
            Interlocked.Add(ref tokensConumed, tokens);
        });

        return (updatesPerformed, tokensConumed);
    }

    private async Task<(int DbUpdates, int Tokens)> UpdateRecordsForIssueAsync(string issueId, VectorStoreCollection<Guid, SemanticSearchRecord> vectorCollection, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = _db.CreateDbContext();

        IssueInfo issue = await db.Issues
            .AsNoTracking()
            .Where(i => i.Id == issueId)
            .Include(i => i.Repository)
            .Include(i => i.User)
            .Include(i => i.PullRequest)
            .Include(i => i.Labels)
            .Include(i => i.Comments)
                .ThenInclude(i => i.User)
            .AsSplitQuery()
            .SingleAsync(cancellationToken);

        List<IngestedEmbeddingRecord> previousRecords = await db.IngestedEmbeddings
            .Where(e => e.ResourceIdentifier == issue.Id)
            .ToListAsync(cancellationToken);

        DateTime lastIssueUpdate = issue.UpdatedAt;
        if (issue.Comments.Count > 0)
        {
            DateTime lastCommentUpdate = issue.Comments.Max(c => c.UpdatedAt);
            if (lastCommentUpdate > lastIssueUpdate)
            {
                lastIssueUpdate = lastCommentUpdate;
            }
        }

        (IngestedEmbeddingRecord[] removed, SemanticSearchRecord[] added, int tokens) = await CreateUpdatedRecordsForIssueAsync(issue, previousRecords, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        foreach (IngestedEmbeddingRecord record in previousRecords)
        {
            record.UpdatedAt = lastIssueUpdate;
        }

        _logger.TraceLog($"{nameof(GitHubSearchService)}: Issue {issue.Repository.FullName}#{issue.Number}: {previousRecords.Count} previous records, {issue.Comments.Count} comments, {removed.Length} removed, {added.Length} added");

        if (removed.Length > 0)
        {
            db.IngestedEmbeddings.RemoveRange(removed);
            await vectorCollection.DeleteAsync(removed.Select(r => r.Id), CancellationToken.None);
        }

        if (added.Length > 0)
        {
            db.IngestedEmbeddings.AddRange(added.Select(r => new IngestedEmbeddingRecord
            {
                Id = r.Key,
                ResourceIdentifier = issue.Id,
                UpdatedAt = lastIssueUpdate,
            }));
            await vectorCollection.UpsertAsync(added, CancellationToken.None);
        }

        return (await db.SaveChangesAsync(CancellationToken.None), tokens);
    }

    private async Task<(IngestedEmbeddingRecord[] RemovedRecords, SemanticSearchRecord[] NewRecords, int Tokens)> CreateUpdatedRecordsForIssueAsync(IssueInfo issue, List<IngestedEmbeddingRecord> previousRecords, CancellationToken cancellationToken)
    {
        string titleInfo = $"{issue.Repository.FullName}#{issue.Number}: {issue.Title}";

        List<(string SubIdentifier, string Text)> rawSections =
        [
            (null, issue.Title),
            (null, titleInfo),
            (null, $"{(issue.PullRequest is null ? "" : "PR ")}{issue.Title} in {issue.Repository.FullName} by {issue.User.Login}"),
            .. GetSections(issue, null, $"{issue.Body}\n\nLabels: {string.Join(", ", issue.Labels.Select(l => l.Name))}", titleInfo).Select(t => ((string)null, t)),
            .. issue.Comments.SelectMany(c => GetSections(issue, c, c.Body, titleInfo).Select(t => (c.Id, t)))
        ];

        List<(string SubIdentifier, string Text, Guid Key)> keyedSections = rawSections
            .Where(section => !string.IsNullOrWhiteSpace(section.Text))
            .DistinctBy(section => section.Text)
            .Select(section => (section.SubIdentifier, section.Text, GetGuidFromSectionHash(issue.Id, section)))
            .ToList();

        if (keyedSections.Count == 0)
        {
            // Ensure there's at least one entry per issue so that the update loop sees the updated timestamp.
            keyedSections.Add((null, titleInfo, GetGuidFromSectionHash(issue.Id, (null, titleInfo))));
        }

        IngestedEmbeddingRecord[] removedRecords = [.. previousRecords.Where(prev => !keyedSections.Any(r => r.Key == prev.Id))];

        keyedSections.RemoveAll(section => previousRecords.Any(prev => prev.Id == section.Key));

        if (keyedSections.Count == 0)
        {
            return (removedRecords, [], 0);
        }

        try
        {
            int tokens = keyedSections.Sum(section => Tokenizer.CountTokens(section.Text));

            List<Embedding<float>> embeddings = [];
            foreach (string[] chunk in keyedSections.Select(section => section.Text).Chunk(1000))
            {
                embeddings.AddRange(await _embeddingGenerator2.GenerateAsync(chunk, cancellationToken: cancellationToken));
            }

            SemanticSearchRecord[] newRecords = keyedSections.Zip(embeddings).Select(pair => new SemanticSearchRecord
            {
                Key = pair.First.Key,
                IssueId = issue.Id,
                SubIdentifier = pair.First.SubIdentifier,
                RepositoryId = issue.RepositoryId,
                Vector = pair.Second.Vector,
            }).ToArray();

            return (removedRecords, newRecords, tokens);
        }
        catch (ClientResultException cre) when (cre.Status == 400)
        {
            _logger.DebugLog($"{nameof(GitHubSearchService)}: Failed to generate embeddings for {issue.HtmlUrl}: {cre}.\nTexts:\n{string.Join("\n\n\n", keyedSections.Select(s => s.Text))}");
            return (removedRecords, [], 0);
        }

        static Guid GetGuidFromSectionHash(string issueId, (string SubIdentifier, string Text) section)
        {
            return new Guid($"{issueId}-{section.SubIdentifier}-{section.Text}".GetUtf8Sha384Hash().AsSpan(0, 16), bigEndian: false);
        }
    }

    private IEnumerable<string> GetSections(IssueInfo issue, CommentInfo comment, string markdown, string titleInfo)
    {
        return SemanticMarkdownChunker.GetSections(Tokenizer, SmallSectionTokenThreshold, issue, comment, markdown, titleInfo);
    }

    public async Task<string> DeleteIssueAndEmbeddingsAsync(string issueId)
    {
        VectorStoreCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(UpdateCollectionName);

        await using GitHubDbContext db = _db.CreateDbContext();

        IssueInfo issue = await db.Issues
            .Where(i => i.Id == issueId)
            .Include(i => i.Comments)
            .Include(i => i.PullRequest)
            .AsSplitQuery()
            .SingleOrDefaultAsync();

        if (issue is null)
        {
            return $"Issue with ID '{issueId}' not found.";
        }

        Guid[] embeddingIds = await db.IngestedEmbeddings
            .Where(e => e.ResourceIdentifier == issueId)
            .Select(i => i.Id)
            .ToArrayAsync(CancellationToken.None);

        db.Issues.Remove(issue);

        if (issue.PullRequest is PullRequestInfo pr)
        {
            db.PullRequests.Remove(pr);
        }

        foreach (CommentInfo comment in issue.Comments)
        {
            db.Comments.Remove(comment);
        }

        int updates = await db.SaveChangesAsync(CancellationToken.None);

        string message = $"{nameof(GitHubSearchService)}: Deleted issue <{issue.HtmlUrl}> ({updates} updates, {embeddingIds.Length} embeddings).";
        _logger.DebugLog(message);

        await vectorCollection.DeleteAsync(embeddingIds);

        return message;
    }

    private sealed class SemanticSearchRecord
    {
        [VectorStoreKey]
        public Guid Key { get; set; }

        [VectorStoreData(IsIndexed = true)]
        public long RepositoryId { get; set; }

        [VectorStoreData]
        public string IssueId { get; set; }

        [VectorStoreData]
        public string SubIdentifier { get; set; }

        [VectorStoreVector(EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
        public ReadOnlyMemory<float> Vector { get; set; }
    }
}
