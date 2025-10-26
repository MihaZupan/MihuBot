using System.ClientModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using MihuBot.DB.Models;
using MihuBot.RuntimeUtils.Search;
using OpenAI;
using Qdrant.Client;
using Qdrant.Client.Grpc;

#nullable enable

#pragma warning disable CA1873 // Avoid potentially expensive logging

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

// Background service that periodically checks the GitHub DB and updates embeddings and FTS as needed.
public sealed class GitHubSemanticSearchIngestionService : BackgroundService
{
    private const int SmallSectionTokenThreshold = 200;

    private readonly IConfigurationService _configuration;
    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly ILogger<GitHubSemanticSearchIngestionService> _logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly QdrantClient _qdrantClient;
    private readonly VectorStore _vectorStore;
    private readonly ServiceConfiguration _serviceConfiguration;

    public static Tokenizer Tokenizer { get; } = TiktokenTokenizer.CreateForModel(GitHubDbContext.Defaults.EmbeddingModel);

    private const int IngestionBatchSize = 500;
    private const int IngestionPeriodMs = 5_000;

    private string UpdateCollectionName => _configuration.TryGet(null, $"{nameof(GitHubSemanticSearchIngestionService)}.UpdateCollection", out string name) ? name : "MihuBotGhSearch";

    public GitHubSemanticSearchIngestionService(IDbContextFactory<GitHubDbContext> db, OpenAIService openAi, ILogger<GitHubSemanticSearchIngestionService> logger, IConfigurationService configurationService, QdrantClient qdrantClient, VectorStore vectorStore, ServiceConfiguration serviceConfiguration)
    {
        _db = db;
        _logger = logger;
        _embeddingGenerator = openAi.GetEmbeddingGenerator(GitHubDbContext.Defaults.EmbeddingModel, secondary: true);
        _configuration = configurationService;
        _qdrantClient = qdrantClient;
        _vectorStore = vectorStore;
        _serviceConfiguration = serviceConfiguration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IngestionPeriodMs));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // TODO
                //if (!OperatingSystem.IsLinux())
                //{
                //    continue;
                //}

                if (_serviceConfiguration.PauseSemanticIngestion)
                {
                    continue;
                }

                try
                {
                    (int updates, int tokens) = await UpdateIngestedEmbeddingsAndFtsAsync(stoppingToken);
                    if (updates > 0)
                    {
                        _logger.LogTrace("Performed {Updates} DB updates, consumed {Tokens} tokens", updates, tokens);
                    }

                    if (tokens > 1_000)
                    {
                        const int TokenLimitPerMinute = 750_000;
                        await Task.Delay(TimeSpan.FromMinutes((double)tokens / TokenLimitPerMinute), stoppingToken);
                    }

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    _logger.LogError(ex, "Update failed ({FailureCount})", consecutiveFailureCount);

                    await Task.Delay(TimeSpan.FromSeconds(consecutiveFailureCount), stoppingToken);
                }
            }
        }
        catch when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception during GH data polling");
        }
    }

    private async Task<(int DbUpdates, int Tokens)> UpdateIngestedEmbeddingsAndFtsAsync(CancellationToken cancellationToken)
    {
        VectorStoreCollection<Guid, SemanticSearchRecord> vectorCollection = _vectorStore.GetCollection<Guid, SemanticSearchRecord>(UpdateCollectionName);

        if (!await vectorCollection.CollectionExistsAsync(cancellationToken))
        {
            await _qdrantClient.CreateCollectionAsync(UpdateCollectionName,
                vectorsConfig: new VectorParams
                {
                    Size = GitHubDbContext.Defaults.EmbeddingDimensions,
                    Distance = Distance.Cosine,
                    OnDisk = true,
                },
                quantizationConfig: new QuantizationConfig
                {
                    Scalar = new ScalarQuantization
                    {
                        Type = QuantizationType.Int8,
                    }
                },
                cancellationToken: cancellationToken);
        }

        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        Stopwatch outdatedQueryStopwatch = Stopwatch.StartNew();

        List<SemanticIngestionBacklogEntry> backlog = await db.SemanticIngestionBacklog
            .OrderBy(i => i.Id)
            .Take(IngestionBatchSize)
            .ToListAsync(cancellationToken);

        HashSet<string> updatedIssueIds = [.. backlog.Select(e => e.IssueId)];

        if (updatedIssueIds.Count == 0)
        {
            return (0, 0);
        }

        _logger.LogTrace("Found {Count} issues to update in {ElapsedMs} ms", updatedIssueIds.Count, (int)outdatedQueryStopwatch.ElapsedMilliseconds);

        int updatesPerformed = 0;
        int tokensConumed = 0;

        await Parallel.ForEachAsync(updatedIssueIds, new ParallelOptions { MaxDegreeOfParallelism = 10 }, async (issueId, _) =>
        {
            (int dbUpdates, int tokens) = await UpdateEmbeddingsAndFtsForIssueAsync(issueId, vectorCollection, cancellationToken);
            Interlocked.Add(ref updatesPerformed, dbUpdates);
            Interlocked.Add(ref tokensConumed, tokens);
        });

        db.SemanticIngestionBacklog.RemoveRange(backlog);
        updatesPerformed += await db.SaveChangesAsync(cancellationToken);

        _logger.LogTrace("Updated ingested embeddings and FTS in {ElapsedMs} ms", (int)outdatedQueryStopwatch.ElapsedMilliseconds);

        return (updatesPerformed, tokensConumed);
    }

    private async Task<(int DbUpdates, int Tokens)> UpdateEmbeddingsAndFtsForIssueAsync(string issueId, VectorStoreCollection<Guid, SemanticSearchRecord> vectorCollection, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        IssueInfo? issue = await db.Issues
            .Where(i => i.Id == issueId)
            .Include(i => i.Repository)
            .Include(i => i.User)
            .Include(i => i.PullRequest)
            .Include(i => i.Labels)
            .Include(i => i.Comments)
                .ThenInclude(i => i.User)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (issue is null)
        {
            Guid[] existing = await db.IngestedEmbeddings
                .AsNoTracking()
                .Where(e => e.IssueId == issueId)
                .Select(e => e.Id)
                .ToArrayAsync(cancellationToken);

            await vectorCollection.DeleteAsync(existing, cancellationToken);

            int updates = await db.IngestedEmbeddings.AsNoTracking().Where(e => e.IssueId == issueId).ExecuteDeleteAsync(cancellationToken);
            updates += await db.TextEntries.AsNoTracking().Where(e => e.IssueId == issueId).ExecuteDeleteAsync(cancellationToken);
            return (updates, 0);
        }

        DateTime lastIssueUpdate = issue.UpdatedAt;
        if (issue.Comments.Count > 0)
        {
            DateTime lastCommentUpdate = issue.Comments.Max(c => c.UpdatedAt);
            if (lastCommentUpdate > lastIssueUpdate)
            {
                lastIssueUpdate = lastCommentUpdate;
            }
        }

        issue.LastSemanticIngestionTime = lastIssueUpdate;

        int embeddingTokens = 0;

        // Update embeddings (SemanticSearch)
        {
            List<Guid> previousRecordIds = await db.IngestedEmbeddings
                .Where(e => e.IssueId == issueId)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken);

            (Guid[] removed, SemanticSearchRecord[] added, embeddingTokens) = await CreateUpdatedEmbeddingRecordsForIssueAsync(issue, previousRecordIds, cancellationToken);

            _logger.LogTrace("SemanticSearch ingestion for {IssueUrl}: {PreviousCount} previous records, {CommentCount} comments, {RemovedCount} removed, {AddedCount} added with {Tokens} tokens",
                issue.HtmlUrl, previousRecordIds.Count, issue.Comments.Count, removed.Length, added.Length, embeddingTokens);

            cancellationToken.ThrowIfCancellationRequested();

            if (removed.Length > 0)
            {
                await vectorCollection.DeleteAsync(removed, cancellationToken);
                db.IngestedEmbeddings.RemoveRange(removed.Select(id => new IngestedEmbeddingRecord { Id = id }));
            }

            if (added.Length > 0)
            {
                await vectorCollection.UpsertAsync(added, cancellationToken);
                db.IngestedEmbeddings.AddRange(added.Select(r => new IngestedEmbeddingRecord { Id = r.Id, RepositoryId = r.RepositoryId, IssueId = r.IssueId }));
            }
        }

        // Update FTS (TextEntries)
        {
            List<Guid> previousRecordIds = await db.TextEntries
                .Where(e => e.IssueId == issueId)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken);

            (Guid[] removed, TextEntry[] added) = CreateUpdatedFtsRecordsForIssue(issue, previousRecordIds);

            _logger.LogTrace("FTS ingestion for {IssueUrl}: {PreviousCount} previous records, {CommentCount} comments, {RemovedCount} removed, {AddedCount} added",
                issue.HtmlUrl, previousRecordIds.Count, issue.Comments.Count, removed.Length, added.Length);

            cancellationToken.ThrowIfCancellationRequested();

            if (removed.Length > 0)
            {
                db.TextEntries.RemoveRange(removed.Select(id => new TextEntry { Id = id }));
            }

            if (added.Length > 0)
            {
                db.TextEntries.AddRange(added);
            }
        }

        return (await db.SaveChangesAsync(CancellationToken.None), embeddingTokens);
    }

    private async Task<(Guid[] RemovedRecords, SemanticSearchRecord[] NewRecords, int Tokens)> CreateUpdatedEmbeddingRecordsForIssueAsync(IssueInfo issue, List<Guid> previousRecords, CancellationToken cancellationToken)
    {
        string titleInfo = $"{issue.Repository.FullName}#{issue.Number}: {issue.Title}";

        List<(string? SubIdentifier, string Text)> rawSections =
        [
            .. GetSections(null, $"{issue.Body}\n\nLabels: {string.Join(", ", issue.Labels.Select(l => l.Name))}", titleInfo).Select(t => ((string?)null, t)),
            .. issue.Comments.SelectMany(c => GetSections(c, c.Body, titleInfo).Select(t => (c.Id, t)))
        ];

        if (issue.User.IsLikelyARealUser())
        {
            rawSections.Add((null, issue.Title));
            rawSections.Add((null, titleInfo));
            rawSections.Add((null, $"{(issue.PullRequest is null ? "" : "PR ")}{issue.Title} in {issue.Repository.FullName} by {issue.User.Login}"));
        }

        List<(string? SubIdentifier, string Text, Guid Key)> keyedSections = rawSections
            .Where(section => !string.IsNullOrWhiteSpace(section.Text))
            .Select(section => (section.SubIdentifier, section.Text, Key: GetGuidFromSectionHash(issue.Id, section)))
            .DistinctBy(section => section.Key)
            .ToList();

        if (keyedSections.Count == 0)
        {
            // Ensure there's at least one entry per issue so that the update loop sees the updated timestamp.
            keyedSections.Add((null, titleInfo, GetGuidFromSectionHash(issue.Id, (null, titleInfo))));
        }

        Guid[] removedRecords = [.. previousRecords.Where(prev => !keyedSections.Any(r => r.Key == prev))];

        keyedSections.RemoveAll(section => previousRecords.Any(prev => prev == section.Key));

        if (keyedSections.Count == 0)
        {
            return (removedRecords, [], 0);
        }

        try
        {
            int tokens = keyedSections.Sum(section => Tokenizer.CountTokens(section.Text));

            List<Embedding<float>> embeddings = [];
            foreach (string[] chunk in keyedSections.Select(section => section.Text).Chunk(100))
            {
                embeddings.AddRange(await _embeddingGenerator.GenerateAsync(chunk, cancellationToken: cancellationToken));
            }

            SemanticSearchRecord[] newRecords = keyedSections.Zip(embeddings).Select(pair => new SemanticSearchRecord
            {
                Id = pair.First.Key,
                IssueId = issue.Id,
                SubIdentifier = pair.First.SubIdentifier,
                RepositoryId = issue.RepositoryId,
                Vector = pair.Second.Vector,
            }).ToArray();

            return (removedRecords, newRecords, tokens);
        }
        catch (ClientResultException cre) when (cre.Status == 400)
        {
            _logger.LogDebug(cre, "Failed to generate embeddings for {IssueUrl}", issue.HtmlUrl);
            return (removedRecords, [], 0);
        }

        IEnumerable<string> GetSections(CommentInfo? comment, string markdown, string titleInfo)
        {
            return SemanticMarkdownChunker.GetSections(Tokenizer, SmallSectionTokenThreshold, issue, comment, markdown, titleInfo);
        }

        static Guid GetGuidFromSectionHash(string issueId, (string? SubIdentifier, string Text) section)
        {
            return $"{issueId}-{section.SubIdentifier}-{section.Text}".GetTruncatedContentHash();
        }
    }

    private static (Guid[] RemovedRecords, TextEntry[] NewRecords) CreateUpdatedFtsRecordsForIssue(IssueInfo issue, List<Guid> previousRecords)
    {
        string titleInfo = $"{issue.Repository.FullName}#{issue.Number}: {issue.Title}";

        List<(string? SubIdentifier, string Text)> sections = !issue.User.IsLikelyARealUser() ? [] :
        [
            (null, $"{issue.HtmlUrl}\nIssue {issue.Title} by {issue.User.Login}\n\n{issue.Body}"),
            .. issue.Comments.Select(c => (c.Id, $"{c.HtmlUrl}\n Comment by {c.User.Login}\n\n{c.Body}"))
        ];

        List<(string? SubIdentifier, string Text, Guid Key)> keyedSections = sections
            .Select(section => (section.SubIdentifier, Text: section.Text.RemoveNullChars()))
            .Select(section => (section.SubIdentifier, section.Text, Key: GetGuidFromSectionHash(issue.Id, section)))
            .DistinctBy(section => section.Key)
            .ToList();

        Guid[] removedRecords = [.. previousRecords.Where(prev => !keyedSections.Any(r => r.Key == prev))];

        keyedSections.RemoveAll(section => previousRecords.Any(prev => prev == section.Key));

        TextEntry[] newRecords = keyedSections.Select(section => new TextEntry
        {
            Id = section.Key,
            IssueId = issue.Id,
            SubIdentifier = section.SubIdentifier,
            RepositoryId = issue.RepositoryId,
            Text = section.Text,
        }).ToArray();

        return (removedRecords, newRecords);

        static Guid GetGuidFromSectionHash(string issueId, (string? SubIdentifier, string Text) section)
        {
            return $"{issueId}-{section.SubIdentifier}-{section.Text}".GetTruncatedContentHash();
        }
    }
}
