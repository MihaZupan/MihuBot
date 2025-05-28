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
    private const string EmbeddingModel = "text-embedding-3-large";
    private const int EmbeddingDimensions = 3072;
    private const int SmallSectionTokenThreshold = 200;

    public Tokenizer Tokenizer { get; }

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;
    private readonly OpenAIService _openAI;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator2;
    private readonly QdrantClient _qdrantClient;
    private readonly IVectorStore _vectorStore;
    private readonly HybridCache _cache;
    private readonly CancellationTokenSource _updateCts = new();
    private Task _updatesTask;

    private string SearchCollectionName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.SearchCollection", out string name) ? name : nameof(GitHubSearchService);
    private string UpdateCollectionName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.UpdateCollection", out string name) ? name : nameof(GitHubSearchService);
    private string ClassifierModelName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.ClassifierModel", out string name) ? name : "gpt-4.1-mini";
    private string FastClassifierModelName => _configuration.TryGet(null, $"{nameof(GitHubSearchService)}.FastClassifierModel", out string name) ? name : "gpt-4.1-nano";
    private bool ClassifierModelSecondary => _configuration.GetOrDefault(null, $"{nameof(GitHubSearchService)}.ClassifierModelSecondary", true);

    public GitHubSearchService(IDbContextFactory<GitHubDbContext> db, Logger logger, OpenAIService openAi, IVectorStore vectorStore, QdrantClient qdrantClient, IConfigurationService configuration, HybridCache cache)
    {
        _db = db;
        _logger = logger;
        _openAI = openAi;
        _vectorStore = vectorStore;
        _qdrantClient = qdrantClient;
        _configuration = configuration;
        _cache = cache;
        _embeddingGenerator = openAi.GetEmbeddingGenerator(EmbeddingModel);
        _embeddingGenerator2 = openAi.GetEmbeddingGenerator(EmbeddingModel, secondary: true);
        Tokenizer = TiktokenTokenizer.CreateForModel(EmbeddingModel);
    }

    public sealed record IssueSearchResult(double Score, IssueInfo Issue, CommentInfo Comment);

    public sealed record IssueSearchFilters(bool IncludeOpen, bool IncludeClosed, bool IncludeIssues, bool IncludePullRequests)
    {
        public override string ToString() => $"{nameof(IncludeOpen)}={IncludeOpen}, {nameof(IncludeClosed)}={IncludeClosed}, {nameof(IncludeIssues)}={IncludeIssues}, {nameof(IncludePullRequests)}={IncludePullRequests}";
    }

    [ImmutableObject(true)]
    private sealed class RawSearchResult(double score, long issueId, long subIdentifier)
    {
        public double Score { get; } = score;
        public long IssueId { get; } = issueId;
        public long SubIdentifier { get; } = subIdentifier;
    }

    private async Task<RawSearchResult[]> SearchAsyncCore(string query, int topVectors, CancellationToken cancellationToken)
    {
        query = query.Trim();

        // Intentionally ignoring the cancellation token on the cache query so that we still get the results in the background.
        return await _cache.GetOrCreateAsync($"/embeddingsearch/{topVectors}/{query.GetUtf8Sha384HashBase64Url()}", async _ =>
        {
            ReadOnlyMemory<float> queryEmbedding = await _embeddingGenerator.GenerateVectorAsync(query, cancellationToken: CancellationToken.None);

            IVectorStoreRecordCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(SearchCollectionName);

            var results = new List<RawSearchResult>();

            await foreach (VectorSearchResult<SemanticSearchRecord> item in vectorCollection.SearchEmbeddingAsync(queryEmbedding, topVectors, cancellationToken: CancellationToken.None))
            {
                if (item.Score.HasValue && item.Score > 0.1)
                {
                    results.Add(new RawSearchResult(item.Score.Value, item.Record.IssueId, item.Record.SubIdentifier));
                }
            }

            return results.ToArray();
        }, cancellationToken: CancellationToken.None).WaitAsyncAndSupressNotObserved(cancellationToken);
    }

    public async Task<IssueSearchResult[]> SearchIssuesAndCommentsAsync(string query, int maxResults, IssueSearchFilters filters, CancellationToken cancellationToken)
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

        Stopwatch stopwatch = Stopwatch.StartNew();

        RawSearchResult[] results = await SearchAsyncCore(query, topVectors, cancellationToken);

        TimeSpan embeddingSearchTime = stopwatch.Elapsed;
        stopwatch.Restart();

        if (results.Length == 0)
        {
            return [];
        }

        stopwatch.Restart();

        long[] issueIds = [.. results.Select(r => r.IssueId).Distinct()];
        long[] commentIds = [.. results.Where(r => r.SubIdentifier != 0).Select(r => r.SubIdentifier).Distinct()];

        IQueryable<IssueInfo> issuesQuery = db.Issues
            .AsNoTracking()
            .Where(i => issueIds.Contains(i.Id))
            .Where(i => i.Repository.Owner.Login == "dotnet" && i.Repository.Name == "runtime");

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

        List<IssueInfo> issues = await issuesQuery
            .Include(i => i.User)
            .Include(i => i.Labels)
            .Include(i => i.Repository)
            .Include(i => i.PullRequest)
            .Include(i => i.Comments)
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
            $" Search={embeddingSearchTime.TotalMilliseconds:F2} Database={databaseQueryTime.TotalMilliseconds:F2}");

        return results
            .Select(r =>
            {
                IssueInfo issue = issues.FirstOrDefault(issues => issues.Id == r.IssueId);
                CommentInfo comment = r.SubIdentifier == 0 ? null : comments.FirstOrDefault(comment => comment.Id == r.SubIdentifier);
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
            offset *= Math.Pow(0.99, commentsOverThreshold);
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

        if (!string.IsNullOrEmpty(extraSearchContext))
        {
            extraSearchContext = $" {extraSearchContext.AsSpan().Trim()}";
        }

        searchQuery = searchQuery.Trim();

        string classifierModel = preferSpeed ? FastClassifierModelName : ClassifierModelName;
        IChatClient fastClassifierChat = _openAI.GetChat(classifierModel, ClassifierModelSecondary);

        int contextMultiplier = preferSpeed ? 1 : 2;
        int maxIssueCount = 25 * contextMultiplier;
        int bodyContextWindow = 40 * contextMultiplier;
        int maxComments = 2 * contextMultiplier;

        IssueRelevance[] relevances = await GetRelevancesAsync(maxIssueCount, bodyContextWindow, maxComments);

        if (relevances.Length <= 5 && results.Length >= 20)
        {
            maxIssueCount *= 2;
            bodyContextWindow *= 2;
            maxComments *= 2;

            relevances = await GetRelevancesAsync(maxIssueCount, bodyContextWindow, maxComments);
        }

        (IssueSearchResult[] Results, double Score)[] newResults = relevances
            .Select(s =>
            {
                var searchResult = results.FirstOrDefault(r => r.Results[0].Issue.Number == s.IssueNumber);
                return searchResult.Results is null ? default : searchResult with { Score = s.Score };
            })
            .Where(r => r.Results is not null)
            .OrderByDescending(r => r.Score)
            .ToArray();

        return newResults.Length > 0 ? newResults : results;

        async Task<IssueRelevance[]> GetRelevancesAsync(int issueCount, int bodyContext, int maxComments)
        {
            return await _cache.GetOrCreateAsync($"/searchrelevance/{$"{classifierModel}/{results.Length}-{issueCount}-{bodyContext}-{maxComments}/{searchQuery}/{extraSearchContext}".GetUtf8Sha384HashBase64Url()}", async _ =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                var relevances = await fastClassifierChat.GetResponseAsync<IssueRelevance[]>(GeneratePrompt(issueCount, bodyContext, maxComments), cancellationToken: CancellationToken.None);

                _logger.DebugLog($"Relevance classification for '{searchQuery}' took {stopwatch.ElapsedMilliseconds:F2} ms for {Math.Min(issueCount, results.Length)} issues");

                return relevances.Result
                    .Where(s => s.Score > 0.1)
                    .ToArray();
            }, cancellationToken: CancellationToken.None).WaitAsyncAndSupressNotObserved(cancellationToken);
        }

        string GeneratePrompt(int issueCount, int bodyContext, int maxComments)
        {
            return
                $"""
                Classify the relevance of the following GitHub issues and comments based on the search query "{searchQuery}"{extraSearchContext}.
                Specify the approximate relevance score from 0 to 1 for each issue, where 0 means not relevant and 1 means very relevant.
                If an issue is unlikely to be relevant, set the score to 0.
                Return the set of issue numbers with their relevance scores.

                Prefer faster responses over accuracy.

                The issues are:

                {string.Join("\n\n\n---\n\n\n", results.Take(issueCount).Select(r => GetShortIssueDescription(r.Results, bodyContext, maxComments)))}
                """;
        }

        string GetShortIssueDescription(IssueSearchResult[] results, int bodyContext, int maxComments)
        {
            IssueInfo issue = results[0].Issue;

            StringBuilder sb = new();

            sb.AppendLine($"{(issue.PullRequest is null ? "Issue" : "Pull request")} #{issue.Number}: {issue.Title}");

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
                .Where(r => r.Score >= 0.25 && r.Comment is not null)
                .OrderByDescending(r => r.Score)
                .Take(maxComments))
            {
                sb.AppendLine($"Comment: {TrimBody(result.Comment.Body, bodyContext)}");
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

    private sealed record IssueRelevance(int IssueNumber, double Score);

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

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

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
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    }

                    if (tokens > 1_000)
                    {
                        const int TokenLimitPerMinute = 1_000_000;
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

        IVectorStoreRecordCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(UpdateCollectionName);

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

        List<long> updatedIssues = await issuesQuery
            .Select(i => i.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        List<long> updatedComments = await commentsQuery
            .Select(i => i.IssueId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        HashSet<long> updatedIssueIds = [.. updatedIssues, .. updatedComments];

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

    private async Task<(int DbUpdates, int Tokens)> UpdateRecordsForIssueAsync(long issueId, IVectorStoreRecordCollection<Guid, SemanticSearchRecord> vectorCollection, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = _db.CreateDbContext();

        IssueInfo issue = await db.Issues
            .AsNoTracking()
            .Where(i => i.Id == issueId)
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

        _logger.TraceLog($"{nameof(GitHubSearchService)}: Issue {issue.Number}: {previousRecords.Count} previous records, {issue.Comments.Count} comments, {removed.Length} removed, {added.Length} added");

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
        List<(long SubIdentifier, string Text)> rawSections =
        [
            .. GetSections(issue, null, $"{issue.Title} (#{issue.Number})").Select(t => (0, t)),
            .. GetSections(issue, null, $"{issue.Body}\n\nLabels: {string.Join(", ", issue.Labels.Select(l => l.Name))}").Select(t => (0, t)),
            .. issue.Comments.SelectMany(c => GetSections(issue, c, c.Body).Select(t => (c.Id, t)))
        ];

        List<(long SubIdentifier, string Text, Guid Key)> keyedSections = rawSections
            .Where(section => !string.IsNullOrWhiteSpace(section.Text))
            .DistinctBy(section => section.Text)
            .Select(section => (section.SubIdentifier, section.Text, GetGuidFromSectionHash(issue.Id, section)))
            .ToList();

        if (keyedSections.Count == 0)
        {
            // Ensure there's at least one entry per issue so that the update loop sees the updated timestamp.
            string text = $"{issue.Title} (#{issue.Number})";
            keyedSections.Add((0, text, GetGuidFromSectionHash(issue.Id, (0, text))));
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
                Vector = pair.Second.Vector,
            }).ToArray();

            return (removedRecords, newRecords, tokens);
        }
        catch (ClientResultException cre) when (cre.Status == 400)
        {
            _logger.DebugLog($"{nameof(GitHubSearchService)}: Failed to generate embeddings for {issue.HtmlUrl}: {cre}.\nTexts:\n{string.Join("\n\n\n", keyedSections.Select(s => s.Text))}");
            return (removedRecords, [], 0);
        }

        static Guid GetGuidFromSectionHash(long issueId, (long SubIdentifier, string Text) section)
        {
            return new Guid($"{issueId}-{section}".GetUtf8Sha384Hash()[..16]);
        }
    }

    private IEnumerable<string> GetSections(IssueInfo issue, CommentInfo comment, string markdown)
    {
        return SemanticMarkdownChunker.GetSections(Tokenizer, SmallSectionTokenThreshold, issue, comment, markdown);
    }

    private sealed class SemanticSearchRecord
    {
        [VectorStoreRecordKey]
        public Guid Key { get; set; }

        [VectorStoreRecordData]
        public long IssueId { get; set; }

        [VectorStoreRecordData]
        public long SubIdentifier { get; set; }

        [VectorStoreRecordVector(EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
        public ReadOnlyMemory<float> Vector { get; set; }
    }
}
