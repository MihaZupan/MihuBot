using System.ClientModel;
using System.Security.Cryptography;
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
    private const string CollectionName = nameof(GitHubSearchService);
    private const int EmbeddingDimensions = 3072;
    private const int SmallSectionTokenThreshold = 200;

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator2;
    private readonly Logger _logger;
    private readonly Tokenizer _tokenizer;
    private readonly QdrantClient _qdrantClient;
    private readonly IConfigurationService _configuration;
    private readonly IVectorStore _vectorStore;
    private readonly CancellationTokenSource _updateCts = new();
    private Task _updatesTask;

    public GitHubSearchService(IDbContextFactory<GitHubDbContext> db, Logger logger, OpenAIService openAi, IVectorStore vectorStore, QdrantClient qdrantClient, IConfigurationService configuration)
    {
        _db = db;
        _logger = logger;
        _vectorStore = vectorStore;
        _qdrantClient = qdrantClient;
        _configuration = configuration;
        _embeddingGenerator = openAi.GetEmbeddingGenerator(EmbeddingModel);
        _embeddingGenerator2 = openAi.GetEmbeddingGenerator(EmbeddingModel, secondary: true);
        _tokenizer = TiktokenTokenizer.CreateForModel(EmbeddingModel);
    }

    public record IssueSearchResult(double Score, IssueInfo Issue);

    private async Task<(long Key, double Score)[]> SearchEmbeddingAsync(ReadOnlyMemory<float> embedding, int topVectors, int topIssues, CancellationToken cancellationToken)
    {
        IVectorStoreRecordCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(CollectionName);

        var nearest = vectorCollection.SearchEmbeddingAsync(embedding, topVectors, cancellationToken: cancellationToken);

        var results = new List<VectorSearchResult<SemanticSearchRecord>>();
        await foreach (VectorSearchResult<SemanticSearchRecord> item in nearest)
        {
            results.Add(item);
        }

        return results
            .Where(r => r.Score.HasValue && r.Score > 0.1)
            .GroupBy(r => r.Record.IssueId)
            .Select(r => (r.Key, Score: GetCombinedScore([.. r.Select(r => r.Score.Value)])))
            .OrderByDescending(r => r.Score)
            .Take(topIssues)
            .ToArray();

        static double GetCombinedScore(List<double> scores)
        {
            double max = scores.Max();
            double offset = 1 - max;

            // Boost issues with multiple potentially related comments.
            double threshold = max * 0.75;
            offset *= Math.Pow(0.99, scores.Count(s => s >= threshold));

            return 1 - offset;
        }
    }

    public async Task<IssueSearchResult[]> SearchIssuesAsync(string query, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        Stopwatch stopwatch = Stopwatch.StartNew();

        ReadOnlyMemory<float> queryEmbedding = await _embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken);

        TimeSpan generateEmbeddingTime = stopwatch.Elapsed;
        stopwatch.Restart();

        if (!_configuration.TryGet(null, $"{nameof(GitHubSearchService)}.TopVectors", out string topVectorsStr) ||
            !int.TryParse(topVectorsStr, out int topVectors) ||
            topVectors is < 1 or > 10_000)
        {
            topVectors = 250;
        }

        if (!_configuration.TryGet(null, $"{nameof(GitHubSearchService)}.TopIssues", out string topIssuesStr) ||
            !int.TryParse(topIssuesStr, out int topIssues) ||
            topIssues is < 1 or > 10_000)
        {
            topIssues = 100;
        }

        (long Key, double Score)[] resultsByIssue = await SearchEmbeddingAsync(queryEmbedding, topVectors, topIssues, cancellationToken);

        TimeSpan embeddingSearchTime = stopwatch.Elapsed;
        stopwatch.Restart();

        if (resultsByIssue.Length == 0)
        {
            return [];
        }

        long[] issueIds = [.. resultsByIssue.Select(r => r.Key)];

        stopwatch.Restart();

        List<IssueInfo> issues = await db.Issues
            .AsNoTracking()
            .Where(i => issueIds.Contains(i.Id))
            .Where(i => i.Repository.Owner.Login == "dotnet" && i.Repository.Name == "runtime")
            .Include(i => i.User)
            .Include(i => i.Labels)
            .Include(i => i.Repository)
            .Include(i => i.PullRequest)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        TimeSpan databaseQueryTime = stopwatch.Elapsed;

        _logger.DebugLog($"Search for '{query}' returned {issues.Count} unique issues," +
            $" Embedding={generateEmbeddingTime.TotalMilliseconds:F2} Search={embeddingSearchTime.TotalMilliseconds:F2} Database={databaseQueryTime.TotalMilliseconds:F2}");

        return resultsByIssue
            .Select(r => new IssueSearchResult(r.Score, issues.First(i => i.Id == r.Key)))
            .OrderByDescending(r => r.Score)
            .ToArray();
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

            //using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            //using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    (int updates, int tokens) = await UpdateIngestedEmbeddingsAsync(cancellationToken);
                    if (updates > 0)
                    {
                        _logger.DebugLog($"{nameof(GitHubSearchService)}: Performed {updates} DB updates, consumed {tokens} tokens");
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
        const int BatchSize = 25;

        await using GitHubDbContext db = _db.CreateDbContext();

        IVectorStoreRecordCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(CollectionName);

        if (!await vectorCollection.CollectionExistsAsync(cancellationToken))
        {
            await _qdrantClient.CreateCollectionAsync(CollectionName,
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

        if (_configuration.TryGet(null, $"{nameof(GitHubSearchService)}.IngestHttpOnly", out string ingestHttpOnlyStr) &&
            bool.TryParse(ingestHttpOnlyStr, out bool ingestHttpOnly) &&
            ingestHttpOnly)
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

        _logger.DebugLog($"{nameof(GitHubSearchService)}: Found {updatedIssueIds.Count} issues to update");

        if (updatedIssueIds.Count == 0)
        {
            return (0, 0);
        }

        int updatesPerformed = 0;
        int tokensConumed = 0;

        await Parallel.ForEachAsync(updatedIssueIds, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (issueId, _) =>
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

        //_logger.DebugLog($"{nameof(GitHubSearchService)}: Issue {issue.Number}: {issue.Comments.Count} comments, {removed.Length} removed, {added.Length} added");

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
        List<string> rawSections =
        [
            .. GetSections(issue, null, $"{issue.Title} (#{issue.Number})"),
            .. GetSections(issue, null, $"{issue.Body}\n\nLabels: {string.Join(", ", issue.Labels.Select(l => l.Name))}"),
            .. issue.Comments.SelectMany(c => GetSections(issue, c, c.Body))
        ];

        List<(string Text, Guid Key)> keyedSections = rawSections
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Unique()
            .Select(text => (text, new Guid(HashText($"{issue.Id}-{text}")[..16])))
            .ToList();

        IngestedEmbeddingRecord[] removedRecords = [.. previousRecords.Where(prev => !keyedSections.Any(r => r.Key == prev.Id))];

        keyedSections.RemoveAll(section => previousRecords.Any(prev => prev.Id == section.Key));

        if (keyedSections.Count == 0)
        {
            return (removedRecords, [], 0);
        }

        try
        {
            int tokens = keyedSections.Sum(section => _tokenizer.CountTokens(section.Text));

            List<Embedding<float>> embeddings = [];
            foreach (string[] chunk in keyedSections.Select(section => section.Text).Chunk(50))
            {
                embeddings.AddRange(await _embeddingGenerator.GenerateAsync(chunk, cancellationToken: cancellationToken));
            }

            SemanticSearchRecord[] newRecords = keyedSections.Zip(embeddings).Select(pair => new SemanticSearchRecord
            {
                Key = pair.First.Key,
                IssueId = issue.Id,
                Vector = pair.Second.Vector,
            }).ToArray();

            return (removedRecords, newRecords, tokens);
        }
        catch (ClientResultException cre) when (cre.Status == 400)
        {
            _logger.DebugLog($"{nameof(GitHubSearchService)}: Failed to generate embeddings for {issue.HtmlUrl}: {cre}.\nTexts:\n{string.Join("\n\n\n", keyedSections.Select(s => s.Text))}");
            return (removedRecords, [], 0);
        }
    }

    private static byte[] HashText(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return SHA384.HashData(bytes);
    }

    private IEnumerable<string> GetSections(IssueInfo issue, CommentInfo comment, string markdown)
    {
        return SemanticMarkdownChunker.GetSections(_tokenizer, SmallSectionTokenThreshold, issue, comment, markdown);
    }

    private sealed class SemanticSearchRecord
    {
        [VectorStoreRecordKey]
        public Guid Key { get; set; }

        [VectorStoreRecordData]
        public long IssueId { get; set; }

        [VectorStoreRecordVector(EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
        public ReadOnlyMemory<float> Vector { get; set; }
    }
}
