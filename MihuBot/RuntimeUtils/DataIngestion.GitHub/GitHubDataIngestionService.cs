using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using Octokit;

#nullable enable

#pragma warning disable CA1873 // Avoid potentially expensive logging

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

public sealed record RepositoryIngestionStats(
    string RepoName,
    int IssueCount,
    int CommentCount,
    int SearchVectorCount,
    int FullTextEntries,
    bool RescanInProgress,
    bool InitialIngestion)
{
    public static async Task<RepositoryIngestionStats> CreateAsync(RepositoryInfo repo, GitHubDbContext db, CancellationToken cancellationToken)
    {
        return new RepositoryIngestionStats(
            repo.FullName,
            await db.Issues.AsNoTracking().Where(i => i.RepositoryId == repo.Id).CountAsync(cancellationToken),
            await db.Comments.AsNoTracking().Where(c => c.Issue.RepositoryId == repo.Id).CountAsync(cancellationToken),
            await db.IngestedEmbeddings.AsNoTracking().Where(s => s.RepositoryId == repo.Id).CountAsync(cancellationToken),
            await db.TextEntries.AsNoTracking().Where(t => t.RepositoryId == repo.Id).CountAsync(cancellationToken),
            RescanInProgress: repo.IssueRescanCursor != null || repo.PullRequestRescanCursor != null || repo.DiscussionRescanCursor != null,
            InitialIngestion: repo.InitialIngestionInProgress);
    }
}

public sealed record GitHubDataIngestionStats(
    int IssueCount,
    int CommentCount,
    int SearchVectorCount,
    RepositoryIngestionStats[] TrackedRepos);

public sealed class GitHubDataIngestionService : BackgroundService
{
    public const long GhostUserId = 10137;
    public const long CopilotUserId = 198982749;
    private const int ContinueRescanChance = 5;

    private static readonly TimeSpan UserInfoRefreshInterval = TimeSpan.FromDays(30);
    private static readonly TimeSpan RepositoryInfoRefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DataPollingOffset = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RepositoryFullRescanInterval = TimeSpan.FromDays(7);

    private static readonly HashSet<string> s_busyRepos = new(
        ["dotnet/aspire", "dotnet/aspnetcore", "dotnet/roslyn", "dotnet/runtime"],
        StringComparer.OrdinalIgnoreCase);

    private static TimeSpan GetIssueUpdateFrequency(RepositoryInfo repo)
    {
        return s_busyRepos.Contains(repo.FullName) ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(2);
    }

    private static TimeSpan GetCommentUpdateFrequency(RepositoryInfo repo)
    {
        return repo.FullName == "dotnet/runtime" ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(2);
    }

    // Update to the current time to force start rescans for all repos. Useful to re-ingest data after adding fields.
    private static readonly DateTime ManualForceRescanCutoffTime = new(2025, 10, 27, 7, 0, 0, DateTimeKind.Utc);

    private readonly ILogger<GitHubDataIngestionService> _logger;
    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly ServiceConfiguration _serviceConfiguration;
    private readonly GitHubClient _github;
    private readonly GithubGraphQLClient _graphQLClient;
    private readonly SimpleRateLimiter _restRateLimit;
    private readonly SimpleRateLimiter _graphQLRateLimit;
    private readonly ConcurrentDictionary<string, long> _repoNameToId = new();
    private HashSet<long> _knownUsers = [];

    public GitHubDataIngestionStats Stats { get; private set; } = new(0, 0, 0, []);

    public GitHubDataIngestionService(ILogger<GitHubDataIngestionService> logger, IDbContextFactory<GitHubDbContext> db, GitHubClient github, IOptions<GitHubClientOptions> githubOptions, ServiceConfiguration serviceConfiguration)
    {
        _logger = logger;
        _db = db;
        _github = github;
        _serviceConfiguration = serviceConfiguration;

        const int GitHubRateLimit = 4_000; // It's actually 5000, but leave some margin
        const int RateLimitBurst = 10;

        _restRateLimit = new SimpleRateLimiter(TimeSpan.FromHours(1) / GitHubRateLimit, RateLimitBurst);

        GitHubClientOptions options = githubOptions.Value;

        if (options.AdditionalTokens is { Length: > 0 } additionalTokens)
        {
            if (additionalTokens.Contains(options.Token, StringComparer.Ordinal))
            {
                throw new ArgumentException("Additional tokens list contains the main token.");
            }

            _graphQLClient = new GithubGraphQLClient(options.ProductName, additionalTokens, logger);
            _graphQLRateLimit = new SimpleRateLimiter(TimeSpan.FromHours(1) / (GitHubRateLimit * additionalTokens.Length), RateLimitBurst * additionalTokens.Length);
        }
        else
        {
            _graphQLClient = new GithubGraphQLClient(options.ProductName, [options.Token], logger);
            _graphQLRateLimit = _restRateLimit;
        }
    }

    private async Task RefreshStatsAsync(CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        RepositoryInfo[] repos = (await db.Repositories
            .AsNoTracking()
            .AsSplitQuery()
            .ToArrayAsync(cancellationToken))
            .OrderBy(r => r.FullName)
            .ToArray();

        List<RepositoryIngestionStats> repoStats = [];

        // This is super expensive, but runs just once every ~5 minutes
        foreach (RepositoryInfo repo in repos)
        {
            repoStats.Add(await RepositoryIngestionStats.CreateAsync(repo, db, cancellationToken));
        }

        Stats = new GitHubDataIngestionStats(
            await db.Issues.AsNoTracking().CountAsync(cancellationToken),
            await db.Comments.AsNoTracking().CountAsync(cancellationToken),
            await db.IngestedEmbeddings.AsNoTracking().CountAsync(cancellationToken),
            [.. repoStats]);
    }

    public async Task<long> TryGetKnownRepositoryIdAsync(string? repoName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(repoName))
        {
            return -1;
        }

        if (_repoNameToId.TryGetValue(repoName, out long id))
        {
            return id;
        }

        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        id = await db.Repositories
            .AsNoTracking()
            .Where(r => r.FullName == repoName)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (id == 0)
        {
            return -1;
        }

        _repoNameToId[repoName] = id;
        return id;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();

            Stopwatch knownUsersRefreshTime = Stopwatch.StartNew();
            Stopwatch lastStatsRefreshTime = new();

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RepositoryInfo? currentRepo = null;

                try
                {
                    if (!lastStatsRefreshTime.IsRunning || lastStatsRefreshTime.Elapsed.TotalMinutes > 5)
                    {
                        await RefreshStatsAsync(stoppingToken);
                        lastStatsRefreshTime.Restart();
                    }

                    if (!OperatingSystem.IsLinux())
                    {
                        continue;
                    }

                    if (_serviceConfiguration.PauseGitHubPolling)
                    {
                        continue;
                    }

                    List<RepositoryInfo> repositories;

                    await using (GitHubDbContext db = await _db.CreateDbContextAsync(stoppingToken))
                    {
                        repositories = await db.Repositories
                            .AsNoTracking()
                            .ToListAsync(stoppingToken);
                    }

                    // Refresh known users list every so often. If any repo is still doing initial ingestion we do it more often to avoid wasted DB fetches.
                    if (_knownUsers.Count == 0 || knownUsersRefreshTime.Elapsed.TotalMinutes > (repositories.Any(r => r.InitialIngestionInProgress) ? 2 : 15))
                    {
                        await using (GitHubDbContext db = await _db.CreateDbContextAsync(stoppingToken))
                        {
                            DateTime staleUserCutoff = DateTime.UtcNow - UserInfoRefreshInterval;

                            _knownUsers = (await db.Users
                                .Where(u => u.EntryUpdatedAt >= staleUserCutoff)
                                .Select(u => u.Id)
                                .ToListAsync(stoppingToken))
                                .ToHashSet();
                        }

                        knownUsersRefreshTime.Restart();
                    }

                    foreach (RepositoryInfo repo in repositories)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        currentRepo = repo;
                        await UpdateRepositoryDataAsync(repo, stoppingToken);
                    }

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    _logger.LogError(ex, "Update failed for '{RepoName}' ({FailureCount})", currentRepo?.FullName, consecutiveFailureCount);

                    if (ex is RateLimitExceededException rateLimitEx)
                    {
                        TimeSpan toWait = rateLimitEx.GetRetryAfterTimeSpan();
                        if (toWait.TotalSeconds > 1)
                        {
                            _logger.LogDebug("GitHub polling toWait={ToWait}", toWait);
                            if (toWait > TimeSpan.FromMinutes(5))
                            {
                                toWait = TimeSpan.FromMinutes(5);
                            }
                            await Task.Delay(toWait, stoppingToken);
                        }
                    }

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

    private async Task UpdateRepositoryDataAsync(RepositoryInfo repository, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        var context = new UpdateContext
        {
            Parent = this,
            DbContext = db,
            RepoId = repository.Id,
        };

        await context.UpdateRepositoryDataAsync(cancellationToken);

        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task OnboardNewRepositoryAsync(string repoOwner, string repoName, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        var repo = await _github.Repository.Get(repoOwner, repoName);

        var context = new UpdateContext
        {
            Parent = this,
            DbContext = db,
            RepoId = repo.Id,
        };

        await context.OnboardNewRepositoryAsync(cancellationToken);

        await db.SaveChangesAsync(CancellationToken.None);

        Stats = Stats with
        {
            TrackedRepos = [.. Stats.TrackedRepos, new RepositoryIngestionStats(repo.FullName, 0, 0, 0, 0, true, true)]
        };
    }

    public async Task<RepositoryInfo?> TryGetRepositoryInfoAsync(string repoName)
    {
        await using GitHubDbContext dbContext = _db.CreateDbContext();

        return await dbContext.Repositories
            .AsNoTracking()
            .Where(r => r.FullName == repoName)
            .Include(r => r.Owner)
            .Include(r => r.Labels)
            .AsSplitQuery()
            .SingleOrDefaultAsync();
    }

    public static void PopulateBasicIssueInfo(IssueInfo info, Issue issue) => UpdateContext.PopulateBasicIssueInfo(info, issue);

    internal sealed class UpdateContext
    {
        private static readonly ApiOptions s_apiOptions = new()
        {
            PageSize = 100,
            PageCount = 50,
        };

        public required GitHubDataIngestionService Parent { get; init; }
        public required GitHubDbContext DbContext { get; init; }
        public required long RepoId { get; init; }

        public SimpleRateLimiter RestRateLimit => Parent._restRateLimit;
        public SimpleRateLimiter GraphQLRateLimit => Parent._graphQLRateLimit;
        public GitHubClient GitHub { get => field ?? Parent._github; set; }
        private GithubGraphQLClient GraphQLClient => Parent._graphQLClient;
        private ILogger<GitHubDataIngestionService> Logger => Parent._logger;

        public int ApiCallsPerformed { get; private set; }
        public int UpdatesPerformed { get; private set; }

        private RepositoryInfo? _repoInfo;
        private bool _refreshedLabelsList;
        private bool _refreshedMilestonesList;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly HashSet<long> _updatedUsers = [];
        private readonly HashSet<string> _issuesMarkedForSemanticIngestion = [];

        private async Task OnRestApiCall(int count, CancellationToken cancellationToken)
        {
            for (int i = 0; i < count; i++)
            {
                ApiCallsPerformed++;

                while (!RestRateLimit.TryEnter())
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        private async Task OnGraphQLCall(int calls, int cost, CancellationToken cancellationToken)
        {
            ApiCallsPerformed += calls;

            for (int i = 0; i < cost; i++)
            {
                while (!GraphQLRateLimit.TryEnter())
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        public async Task OnboardNewRepositoryAsync(CancellationToken cancellationToken)
        {
            if (await DbContext.Repositories.AnyAsync(r => r.Id == RepoId, cancellationToken))
            {
                throw new InvalidOperationException("Repository is already onboarded.");
            }

            _repoInfo = await GetOrUpdateRepositoryInfoAsync(cancellationToken);
            _repoInfo.InitialIngestionInProgress = true;
            _repoInfo.UpdateRescanCursors(string.Empty);
        }

        public async Task UpdateRepositoryDataAsync(CancellationToken cancellationToken)
        {
            _repoInfo = await GetOrUpdateRepositoryInfoAsync(cancellationToken);

            if (_repoInfo.Archived)
            {
                return;
            }

            if (!_repoInfo.InitialIngestionInProgress)
            {
                await PollUpdatesAsync(cancellationToken);
            }

            bool shouldRefreshStats = false;

            // LastForceRescanStartTime is a separate field from LastFullRescanStartTime so that we can restart an in-progress rescan if needed.
            if (_repoInfo.LastForceRescanStartTime < ManualForceRescanCutoffTime)
            {
                _repoInfo.LastForceRescanStartTime = DateTime.UtcNow;
                _repoInfo.UpdateRescanCursors(string.Empty);
                shouldRefreshStats = true;
            }

            if (_repoInfo.IssueRescanCursor is not null || _repoInfo.PullRequestRescanCursor is not null || _repoInfo.DiscussionRescanCursor is not null)
            {
                if (_repoInfo.IssueRescanCursor == string.Empty && _repoInfo.PullRequestRescanCursor == string.Empty && _repoInfo.DiscussionRescanCursor == string.Empty)
                {
                    _repoInfo.LastFullRescanStartTime = DateTime.UtcNow;
                }

                if (!_repoInfo.InitialIngestionInProgress && Random.Shared.Next(ContinueRescanChance) != 0)
                {
                    // This is a background rescan, so only do a bit of it now and then
                    return;
                }

                if (_repoInfo.IssueRescanCursor is not null)
                {
                    await ContinueIssuesRescanAsync(cancellationToken);
                }

                if (_repoInfo.PullRequestRescanCursor is not null)
                {
                    await ContinuePullRequestsRescanAsync(cancellationToken);
                }

                if (_repoInfo.DiscussionRescanCursor is not null)
                {
                    await ContinueDiscussionsRescanAsync(cancellationToken);
                }

                if (_repoInfo.IssueRescanCursor is null && _repoInfo.PullRequestRescanCursor is null && _repoInfo.DiscussionRescanCursor is null)
                {
                    // Completed full rescan
                    shouldRefreshStats = true;
                    _repoInfo.InitialIngestionInProgress = false;
                    _repoInfo.LastFullRescanTime = DateTime.UtcNow;

                    // Reset the update times so that polling logic doesn't look too far back
                    _repoInfo.LastIssuesUpdate = DateTime.UtcNow - TimeSpan.FromDays(1);
                    _repoInfo.LastIssueCommentsUpdate = DateTime.UtcNow - TimeSpan.FromDays(1);
                    _repoInfo.LastPullRequestReviewCommentsUpdate = DateTime.UtcNow - TimeSpan.FromDays(1);

                    UpdatesPerformed += await DbContext.SaveChangesAsync(cancellationToken);
                    await RemoveAllTransferredOrDeletedEntriesAsync(cancellationToken);
                }
            }
            else if (DateTime.UtcNow - _repoInfo.LastFullRescanTime > RepositoryFullRescanInterval)
            {
                // Reschedule a full rescan
                shouldRefreshStats = true;
                _repoInfo.UpdateRescanCursors(string.Empty);
            }

            UpdatesPerformed += await DbContext.SaveChangesAsync(cancellationToken);

            if (shouldRefreshStats)
            {
                var newRepoStats = await RepositoryIngestionStats.CreateAsync(_repoInfo, DbContext, cancellationToken);

                Parent.Stats = Parent.Stats with
                {
                    TrackedRepos = [.. Parent.Stats.TrackedRepos.Where(r => r.RepoName != newRepoStats.RepoName).Append(newRepoStats).OrderBy(r => r.RepoName)]
                };
            }
        }

        private async Task RemoveAllTransferredOrDeletedEntriesAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_repoInfo is not null);
            Debug.Assert(_repoInfo.IssueRescanCursor is null);
            Debug.Assert(_repoInfo.PullRequestRescanCursor is null);
            Debug.Assert(_repoInfo.DiscussionRescanCursor is null);

            DateTime deleteEntriesLastObservedBefore = _repoInfo.LastForceRescanStartTime;

            // This is expected to be a fairly small list.
            List<IssueInfo> issuesToDelete = await DbContext.Issues
                .Where(i => i.RepositoryId == RepoId && i.LastObservedDuringFullRescanTime < deleteEntriesLastObservedBefore)
                .Include(i => i.Comments)
                .Include(i => i.Assignees)
                .Include(i => i.Labels)
                .AsSplitQuery()
                .ToListAsync(cancellationToken);

            if (issuesToDelete.Count > 0)
            {
                Logger.LogInformation("Found {Count} transferred/deleted issues to remove from repository {RepoName}.", issuesToDelete.Count, _repoInfo.FullName);

                foreach (IssueInfo issue in issuesToDelete)
                {
                    DbContext.Comments.RemoveRange(issue.Comments);
                    issue.Assignees.Clear();
                    issue.Labels.Clear();
                    DbContext.Issues.Remove(issue);
                }

                MarkUpdatedIssuesForSemanticIngestion([.. issuesToDelete.Select(i => i.Id)]);
            }

            // We don't currently remove deleted comments on existing issues
        }

        private async Task PollUpdatesAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_repoInfo is not null);

            DateTime startTime = DateTime.UtcNow;
            if (startTime - _repoInfo.LastIssuesUpdate >= GetIssueUpdateFrequency(_repoInfo))
            {
                IReadOnlyList<Issue> issues = await GitHub.Issue.GetAllForRepository(RepoId, new RepositoryIssueRequest
                {
                    Since = _repoInfo.LastIssuesUpdate - DataPollingOffset,
                    SortProperty = IssueSort.Updated,
                    SortDirection = SortDirection.Descending,
                    Filter = IssueFilter.All,
                    State = ItemStateFilter.All,
                }, s_apiOptions);

                Logger.LogTrace("Found {Count} updated issues since {LastUpdate}", issues.Count, _repoInfo.LastIssuesUpdate);

                await OnRestApiCall((issues.Count / s_apiOptions.PageSize!.Value) + 1, cancellationToken);

                await UpdateIssueInfosAsync([.. issues], willAddNewComments: false, cancellationToken);

                _repoInfo.LastIssuesUpdate = DeduceLastUpdateTime(issues.Select(i => (i.UpdatedAt ?? i.CreatedAt).UtcDateTime).ToList());
                UpdatesPerformed += await DbContext.SaveChangesAsync(cancellationToken);
            }

            startTime = DateTime.UtcNow;
            if (startTime - _repoInfo.LastIssueCommentsUpdate >= GetCommentUpdateFrequency(_repoInfo))
            {
                IReadOnlyList<IssueComment> issueComments = await GitHub.Issue.Comment.GetAllForRepository(RepoId, new IssueCommentRequest
                {
                    Since = _repoInfo.LastIssueCommentsUpdate - DataPollingOffset,
                    Sort = IssueCommentSort.Updated,
                    Direction = SortDirection.Descending,
                }, s_apiOptions);

                Logger.LogTrace("Found {Count} updated issue comments since {LastUpdate}", issueComments.Count, _repoInfo.LastIssueCommentsUpdate);

                await OnRestApiCall((issueComments.Count / s_apiOptions.PageSize!.Value) + 1, cancellationToken);

                await UpdateIssueCommentInfosAsync([.. issueComments], cancellationToken);

                _repoInfo.LastIssueCommentsUpdate = DeduceLastUpdateTime(issueComments.Select(comment => (comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime).ToList());
                UpdatesPerformed += await DbContext.SaveChangesAsync(cancellationToken);
            }

            startTime = DateTime.UtcNow;
            if (startTime - _repoInfo.LastPullRequestReviewCommentsUpdate >= GetCommentUpdateFrequency(_repoInfo))
            {
                IReadOnlyList<PullRequestReviewComment> prReviewComments = await GitHub.PullRequest.ReviewComment.GetAllForRepository(RepoId, new PullRequestReviewCommentRequest
                {
                    Since = _repoInfo.LastPullRequestReviewCommentsUpdate - DataPollingOffset,
                    Sort = PullRequestReviewCommentSort.Updated,
                    Direction = SortDirection.Descending,
                }, s_apiOptions);

                Logger.LogTrace("Found {Count} updated PR review comments since {LastUpdate}", prReviewComments.Count, _repoInfo.LastPullRequestReviewCommentsUpdate);

                await OnRestApiCall((prReviewComments.Count / s_apiOptions.PageSize!.Value) + 1, cancellationToken);

                await UpdatePullRequestReviewCommentInfosAsync([.. prReviewComments], cancellationToken);

                _repoInfo.LastPullRequestReviewCommentsUpdate = DeduceLastUpdateTime(prReviewComments.Select(comment => (comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime).ToList());
            }

            UpdatesPerformed += await DbContext.SaveChangesAsync(cancellationToken);

            if (ApiCallsPerformed > 0 || UpdatesPerformed > 0)
            {
                Logger.LogTrace("Finished updating {Repository}. {ApiCallsPerformed} API calls, {UpdatesPerformed} DB updates.", _repoInfo.FullName, ApiCallsPerformed, UpdatesPerformed);
            }
        }

        private DateTime DeduceLastUpdateTime(List<DateTime> dates)
        {
            if (dates.Count == 0)
            {
                return _startTime;
            }

            DateTime min = dates.Min();
            DateTime max = dates.Max();

            // Recent update. Next request will fetch everything anyway.
            if (dates.Count < s_apiOptions.PageSize!.Value / 2)
            {
                return _startTime;
            }

            if (// This wasn't a recent change.
                _startTime - max > TimeSpan.FromDays(1) ||
                // All updates are within a few seconds of each other.
                max - min < DataPollingOffset)
            {
                return max;
            }

            if (TryGetMaxWithoutRecent(TimeSpan.FromMinutes(60), out DateTime last) ||
                TryGetMaxWithoutRecent(TimeSpan.FromMinutes(30), out last) ||
                TryGetMaxWithoutRecent(TimeSpan.FromMinutes(15), out last) ||
                TryGetMaxWithoutRecent(TimeSpan.FromMinutes(5), out last) ||
                TryGetMaxWithoutRecent(TimeSpan.FromMinutes(1), out last))
            {
                return last;
            }

            return min + ((max - min) / 2);

            bool TryGetMaxWithoutRecent(TimeSpan recentThreshold, out DateTime max)
            {
                int recentUpdates = dates.Count(d => _startTime - d < recentThreshold);
                if (recentUpdates < dates.Count / 2)
                {
                    max = dates.Where(d => _startTime - d > recentThreshold).Max();
                    return true;
                }

                max = default;
                return false;
            }
        }

        private async Task ContinueIssuesRescanAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_repoInfo is not null);
            Debug.Assert(_repoInfo.IssueRescanCursor is not null);

            var (issues, calls, cost) = await GraphQLClient.GetIssuesAndComments(_repoInfo.Owner.Login, _repoInfo.Name, _repoInfo.IssueRescanCursor, cancellationToken);
            await OnGraphQLCall(calls, cost, cancellationToken);

            await UpdateIssueInfosAsync(issues.Nodes, IssueType.Issue, cancellationToken);

            await UpdateCommentInfosAsync([.. issues.Nodes.SelectMany(i => i.Comments.Nodes.Select(c => (i.Id, c, isPrReviewComment: false)))], cancellationToken);

            _repoInfo.IssueRescanCursor = issues.PageInfo.HasNextPage ? issues.PageInfo.EndCursor : null;
        }

        private async Task ContinuePullRequestsRescanAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_repoInfo is not null);
            Debug.Assert(_repoInfo.PullRequestRescanCursor is not null);

            var (pullRequests, calls, cost) = await GraphQLClient.GetPullRequestsAndComments(_repoInfo.Owner.Login, _repoInfo.Name, _repoInfo.PullRequestRescanCursor, cancellationToken);
            await OnGraphQLCall(calls, cost, cancellationToken);

            await UpdateIssueInfosAsync([.. pullRequests.Nodes.Select(pr => pr.AsIssue())], IssueType.PullRequest, cancellationToken);

            await UpdatePullRequestInfosAsync(pullRequests.Nodes, cancellationToken);

            var comments = pullRequests.Nodes.SelectMany(pr =>
                pr.Comments.Nodes.Select(c => (pr.Id, c, isPrReviewComment: false))
                .Concat(pr.Reviews.Nodes.Select(review => (pr.Id, review.AsComment(), isPrReviewComment: true)))
                .Concat(pr.Reviews.Nodes.SelectMany(review => review.Comments.Nodes.Select(c => (pr.Id, c, isPrReviewComment: true)))));

            await UpdateCommentInfosAsync([.. comments], cancellationToken);

            _repoInfo.PullRequestRescanCursor = pullRequests.PageInfo.HasNextPage ? pullRequests.PageInfo.EndCursor : null;
        }

        private async Task ContinueDiscussionsRescanAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_repoInfo is not null);
            Debug.Assert(_repoInfo.DiscussionRescanCursor is not null);

            var (discussions, calls, cost) = await GraphQLClient.GetDiscussionsAndComments(_repoInfo.Owner.Login, _repoInfo.Name, _repoInfo.DiscussionRescanCursor, cancellationToken);
            await OnGraphQLCall(calls, cost, cancellationToken);

            await UpdateIssueInfosAsync([.. discussions.Nodes.Select(disc => disc.AsIssue())], IssueType.Discussion, cancellationToken);

            var comments = discussions.Nodes.SelectMany(disc =>
                disc.Comments.Nodes.Select(c => (disc.Id, c.AsComment(), isPrReviewComment: false))
                .Concat(disc.Comments.Nodes.SelectMany(c => c.Replies.Nodes.Select(r => (disc.Id, r, isPrReviewComment: false)))));

            await UpdateCommentInfosAsync([.. comments], cancellationToken);

            _repoInfo.DiscussionRescanCursor = discussions.PageInfo.HasNextPage ? discussions.PageInfo.EndCursor : null;
        }

        private static bool ShouldUpdateUserInfo(long currentUserId, long newUser)
        {
            return
                currentUserId == 0 ||
                currentUserId == newUser ||
                newUser != GhostUserId;
        }

        private async Task<RepositoryInfo> GetOrUpdateRepositoryInfoAsync(CancellationToken cancellationToken)
        {
            RepositoryInfo? info = await DbContext.Repositories
                .Where(r => r.Id == RepoId)
                .Include(r => r.Owner)
                .Include(r => r.Labels)
                .Include(r => r.Milestones)
                .AsSplitQuery()
                .SingleOrDefaultAsync(cancellationToken);

            if (info is not null && DateTime.UtcNow - info.LastRepositoryMetadataUpdate < RepositoryInfoRefreshInterval)
            {
                return info;
            }

            await OnRestApiCall(1, cancellationToken);
            Repository repo = await GitHub.Repository.Get(RepoId);

            if (info is null)
            {
                info = new RepositoryInfo
                {
                    Id = repo.Id
                };
                DbContext.Repositories.Add(info);
            }

            info.Id = repo.Id;
            info.NodeIdentifier = repo.NodeId;
            info.HtmlUrl = repo.HtmlUrl;
            info.Name = repo.Name?.RemoveNullChars();
            info.FullName = repo.FullName?.RemoveNullChars();
            info.Description = repo.Description?.RemoveNullChars();
            info.CreatedAt = repo.CreatedAt.UtcDateTime;
            info.UpdatedAt = repo.UpdatedAt.UtcDateTime;
            info.Private = repo.Private;
            info.Archived = repo.Archived;
            info.LastRepositoryMetadataUpdate = DateTime.UtcNow;
            info.LastIssuesUpdate = info.LastIssuesUpdate == default ? repo.CreatedAt.UtcDateTime : info.LastIssuesUpdate;
            info.LastIssueCommentsUpdate = info.LastIssueCommentsUpdate == default ? repo.CreatedAt.UtcDateTime : info.LastIssueCommentsUpdate;
            info.LastPullRequestReviewCommentsUpdate = info.LastPullRequestReviewCommentsUpdate == default ? repo.CreatedAt.UtcDateTime : info.LastPullRequestReviewCommentsUpdate;

            if (ShouldUpdateUserInfo(info.OwnerId, repo.Owner.Id))
            {
                info.OwnerId = repo.Owner.Id;
                await UpdateUserAsync(repo.Owner, cancellationToken);
            }

            await UpdateRepositoryLabelsAsync(info, cancellationToken);

            await UpdateRepositoryMilestonesAsync(info, cancellationToken);

            return info;
        }

        private async Task UpdateRepositoryLabelsAsync(RepositoryInfo info, CancellationToken cancellationToken)
        {
            if (_refreshedLabelsList)
            {
                return;
            }

            _refreshedLabelsList = true;

            await OnRestApiCall(1, cancellationToken);
            IReadOnlyList<Label> labels = await GitHub.Issue.Labels.GetAllForRepository(RepoId, s_apiOptions);

            LabelInfo[] existingLabels = await DbContext.Labels
                .Where(l => l.RepositoryId == RepoId)
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken);

            List<LabelInfo> allLabels = [.. existingLabels];

            foreach (Label label in labels)
            {
                string labelId = label.NodeId;
                LabelInfo? labelInfo = existingLabels.FirstOrDefault(el => el.Id == labelId);

                if (labelInfo is null)
                {
                    labelInfo = new LabelInfo
                    {
                        Id = labelId
                    };
                    DbContext.Labels.Add(labelInfo);
                    allLabels.Add(labelInfo);
                }

                labelInfo.Id = labelId;
                labelInfo.Url = label.Url;
                labelInfo.Name = label.Name?.RemoveNullChars();
                labelInfo.Color = label.Color;
                labelInfo.Description = label.Description?.RemoveNullChars();
                labelInfo.RepositoryId = RepoId;
            }

            // Note that we're not currently removing deleted labels from the DB.
            info.Labels = allLabels;
        }

        private async Task UpdateRepositoryMilestonesAsync(RepositoryInfo info, CancellationToken cancellationToken)
        {
            if (_refreshedMilestonesList)
            {
                return;
            }

            _refreshedMilestonesList = true;

            await OnRestApiCall(1, cancellationToken);
            IReadOnlyList<Milestone> milestones = await GitHub.Issue.Milestone.GetAllForRepository(RepoId, s_apiOptions);

            MilestoneInfo[] existingMilestones = await DbContext.Milestones
                .Where(l => l.RepositoryId == RepoId)
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken);

            List<MilestoneInfo> allMilestones = [.. existingMilestones];

            foreach (Milestone milestone in milestones)
            {
                string milestoneId = milestone.NodeId;
                MilestoneInfo? milestoneInfo = existingMilestones.FirstOrDefault(em => em.Id == milestoneId);

                if (milestoneInfo is null)
                {
                    milestoneInfo = new MilestoneInfo
                    {
                        Id = milestoneId
                    };
                    DbContext.Milestones.Add(milestoneInfo);
                    allMilestones.Add(milestoneInfo);
                }

                milestoneInfo.Id = milestoneId;
                milestoneInfo.Url = milestone.HtmlUrl;
                milestoneInfo.Title = milestone.Title?.RemoveNullChars();
                milestoneInfo.Description = milestone.Description?.RemoveNullChars();
                milestoneInfo.RepositoryId = RepoId;
                milestoneInfo.Number = milestone.Number;
                milestoneInfo.OpenIssueCount = milestone.OpenIssues;
                milestoneInfo.ClosedIssueCount = milestone.ClosedIssues;
                milestoneInfo.CreatedAt = milestone.CreatedAt.UtcDateTime;
                milestoneInfo.UpdatedAt = milestone.UpdatedAt?.UtcDateTime;
                milestoneInfo.ClosedAt = milestone.ClosedAt?.UtcDateTime;
                milestoneInfo.DueOn = milestone.DueOn?.UtcDateTime;
            }

            // Note that we're not currently removing deleted milestones from the DB.
            info.Milestones = allMilestones;
        }

        private async Task UpdateUserAsync(User user, CancellationToken cancellationToken)
        {
            await UpdateUserAsync(user, user.Id, user.Login, cancellationToken);
        }

        private async Task UpdateUserAsync(User? user, long userId, string? userLogin, CancellationToken cancellationToken)
        {
            if (Parent._knownUsers.Contains(userId) || !_updatedUsers.Add(userId))
            {
                return;
            }

            await UpdateUserAsyncCore(user, userId, userLogin, cancellationToken);
        }

        private async Task UpdateUserAsyncCore(User? user, long userId, string? userLogin, CancellationToken cancellationToken)
        {
            Debug.Assert(Parent._knownUsers.Contains(userId) || _updatedUsers.Contains(userId));

            UserInfo? info = await DbContext.Users.SingleOrDefaultAsync(u => u.Id == userId, CancellationToken.None);

            if (info is not null && DateTime.UtcNow - info.EntryUpdatedAt < UserInfoRefreshInterval)
            {
                return;
            }

            try
            {
                if (user is null || userId is not (GhostUserId or CopilotUserId))
                {
                    await OnRestApiCall(1, cancellationToken);
                    user = await GitHub.User.Get(userLogin);
                }
            }
            catch (NotFoundException)
            {
                Logger.LogDebug("User {UserId} {UserLogin} not found.", userId, userLogin);

                if (info is not null)
                {
                    // We already have some info about this user.
                    info?.EntryUpdatedAt = DateTime.UtcNow;
                    return;
                }
            }

            if (info is null)
            {
                info = new UserInfo
                {
                    Id = userId
                };
                DbContext.Users.Add(info);
            }

            info.Id = userId;
            info.NodeIdentifier = user?.NodeId;
            info.Login = userLogin?.RemoveNullChars();
            info.Name = user?.Name?.RemoveNullChars();
            info.HtmlUrl = user?.HtmlUrl;
            info.Followers = user?.Followers ?? 0;
            info.Following = user?.Following ?? 0;
            info.Company = user?.Company?.RemoveNullChars();
            info.Location = user?.Location?.RemoveNullChars();
            info.Bio = user?.Bio?.RemoveNullChars();
            info.Type = user?.Type;
            info.CreatedAt = user?.CreatedAt.UtcDateTime ?? default;
            info.EntryUpdatedAt = DateTime.UtcNow;

            Logger.LogTrace("Updated user metadata {UserId}: {UserLogin} ({UserName})", info.Id, info.Login, info.Name);
        }

        private async Task UpdateUserInfosAsync<T>((T state, long userId, string userLogin)[] issues, Func<T, long> idGetter, Action<T, long> idSetter, CancellationToken cancellationToken)
        {
            List<(long userId, string userLogin)> needsUpdating = [];

            foreach ((T state, long userId, string userLogin) in issues)
            {
                if (ShouldUpdateUserInfo(idGetter(state), userId))
                {
                    idSetter(state, userId);

                    if (!Parent._knownUsers.Contains(userId) && _updatedUsers.Add(userId))
                    {
                        needsUpdating.Add((userId, userLogin));
                    }
                }
            }

            if (needsUpdating.Count == 0)
            {
                return;
            }

            long[] userIds = [.. needsUpdating.Select(nu => nu.userId)];

            UserInfo[] existingInfos = await DbContext.Users
                .Where(u => userIds.Contains(u.Id))
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken);

            needsUpdating = needsUpdating
                .Where(user =>
                {
                    UserInfo? info = existingInfos.FirstOrDefault(info => info.Id == user.userId);
                    return info is null || DateTime.UtcNow - info.EntryUpdatedAt >= UserInfoRefreshInterval;
                })
                .ToList();

            if (needsUpdating.Count == 0)
            {
                return;
            }

            var (users, calls, cost) = await GraphQLClient.GetUsers([.. needsUpdating.Select(nu => nu.userLogin)], cancellationToken);
            await OnGraphQLCall(calls, cost, cancellationToken);

            List<(long userId, string userLogin)> notFoundUsers = [];

            for (int i = 0; i < users.Length; i++)
            {
                long userId = needsUpdating[i].userId;
                GitHubGraphQL.UserModel user = users[i];
                UserInfo? info = existingInfos.FirstOrDefault(u => u.Id == userId);

                if (user is null)
                {
                    notFoundUsers.Add((needsUpdating[i].userId, needsUpdating[i].userLogin));
                    continue;
                }

                if (info is null)
                {
                    info = new UserInfo
                    {
                        Id = userId
                    };
                    DbContext.Users.Add(info);
                }

                info.Id = userId;
                info.NodeIdentifier = user?.Id;
                info.Login = user?.Login?.RemoveNullChars();
                info.Name = user?.Name?.RemoveNullChars();
                info.HtmlUrl = user?.Url;
                info.Followers = user?.Followers?.TotalCount ?? 0;
                info.Following = user?.Following?.TotalCount ?? 0;
                info.Company = user?.Company?.RemoveNullChars();
                info.Location = user?.Location?.RemoveNullChars();
                info.Bio = user?.Bio?.RemoveNullChars();
                info.Type = AccountType.User;
                info.CreatedAt = user?.CreatedAt ?? default;
                info.EntryUpdatedAt = DateTime.UtcNow;

                Logger.LogTrace("Updated user metadata {UserId}: {UserLogin} ({UserName})", info.Id, info.Login, info.Name);
            }

            foreach ((long userId, string userLogin) in notFoundUsers)
            {
                await UpdateUserAsyncCore(null, userId, userLogin, cancellationToken);
            }
        }

        public static void PopulateBasicIssueInfo(IssueInfo info, Issue issue)
        {
            ArgumentNullException.ThrowIfNull(issue);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(info.RepositoryId);

            info.Id = issue.NodeId;
            info.HtmlUrl = issue.HtmlUrl;
            info.Number = issue.Number;
            info.Title = issue.Title?.RemoveNullChars();
            info.Body = issue.Body?.RemoveNullChars();
            info.State = issue.State.Value;
            info.CreatedAt = issue.CreatedAt.UtcDateTime;
            info.UpdatedAt = (issue.UpdatedAt ?? issue.CreatedAt).UtcDateTime;
            info.ClosedAt = issue.ClosedAt?.UtcDateTime;
            info.Locked = issue.Locked;
            info.ActiveLockReason = issue.ActiveLockReason?.Value;

            PopulateIssueReactions(info, issue.Reactions);
        }

        public static void PopulateBasicIssueInfo(IssueInfo info, GitHubGraphQL.IssueModel issue)
        {
            ArgumentNullException.ThrowIfNull(issue);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(info.RepositoryId);

            info.Id = issue.Id;
            info.HtmlUrl = issue.Url;
            info.Number = issue.Number;
            info.Title = issue.Title?.RemoveNullChars();
            info.Body = issue.Body?.RemoveNullChars();
            info.State = ParseItemState(issue.State);
            info.CreatedAt = issue.CreatedAt;
            info.UpdatedAt = issue.UpdatedAt;
            info.ClosedAt = issue.ClosedAt;
            info.Locked = issue.Locked;
            info.ActiveLockReason = ParseLockReason(issue.ActiveLockReason);
            info.AuthorAssociation = ParseAuthorAssociation(issue.AuthorAssociation);

            PopulateIssueReactions(info, ParseReactions(issue.ReactionGroups));
        }

        private static void PopulateIssueReactions(IssueInfo info, ReactionSummary reactions)
        {
            info.Plus1 = reactions.Plus1;
            info.Minus1 = reactions.Minus1;
            info.Laugh = reactions.Laugh;
            info.Confused = reactions.Confused;
            info.Eyes = reactions.Eyes;
            info.Heart = reactions.Heart;
            info.Hooray = reactions.Hooray;
            info.Rocket = reactions.Rocket;
        }

        private async Task<IssueInfo[]> GetOrCreateIssueInfosAsync((string id, string[] labelIds, string? milestoneId)[] issues, CancellationToken cancellationToken)
        {
            Debug.Assert(_repoInfo is not null);

            string[] issueIds = [.. issues.Select(i => i.id)];

            MarkUpdatedIssuesForSemanticIngestion(issueIds);

            IssueInfo[] existingInfos = await DbContext.Issues
                .Where(i => issueIds.Contains(i.Id))
                .Include(i => i.Labels)
                .Include(i => i.Assignees)
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken);

            IssueInfo[] results = new IssueInfo[issues.Length];

            for (int i = 0; i < issues.Length; i++)
            {
                string issueId = issueIds[i];
                string[] issueLabelIds = issues[i].labelIds;
                string? milestoneId = issues[i].milestoneId;
                IssueInfo? info = existingInfos.FirstOrDefault(issue => issue.Id == issueId);

                if (info is null)
                {
                    info = new IssueInfo
                    {
                        Id = issueId
                    };
                    DbContext.Issues.Add(info);
                }

                info.RepositoryId = RepoId;

                // Check for any new labels that we don't know about yet for this repo
                if (issueLabelIds.Any(issueLabelId => !_repoInfo.Labels.Any(repoLabel => issueLabelId == repoLabel.Id)))
                {
                    await UpdateRepositoryLabelsAsync(_repoInfo, cancellationToken);
                }

                // Assign the labels that we know about
                info.Labels = [.. _repoInfo.Labels.Where(repoLabel => issueLabelIds.Contains(repoLabel.Id))];

                // Check for any new milestone that we don't know about yet for this repo
                if (milestoneId is not null && !_repoInfo.Milestones.Any(m => m.Id == milestoneId))
                {
                    await UpdateRepositoryMilestonesAsync(_repoInfo, cancellationToken);
                }

                // Assign the milestone if we know about it
                info.MilestoneId = _repoInfo.Milestones.FirstOrDefault(m => m.Id == milestoneId)?.Id;

                // This will get populated later, for now just avoid NREs.
                info.Assignees ??= [];

                results[i] = info;
            }

            return results;
        }

        private async Task<IssueInfo[]> UpdateIssueInfosAsync(Issue[] issues, bool willAddNewComments, CancellationToken cancellationToken)
        {
            if (issues.Length == 0)
            {
                return [];
            }

            IssueInfo[] infos = await GetOrCreateIssueInfosAsync([.. issues.Select(i => (i.NodeId, i.Labels.Select(l => l.NodeId).ToArray(), i.Milestone?.NodeId))], cancellationToken);

            for (int i = 0; i < issues.Length; i++)
            {
                IssueInfo info = infos[i];
                Issue issue = issues[i];

                PopulateBasicIssueInfo(info, issue);

                if (ShouldUpdateUserInfo(info.UserId, issue.User.Id))
                {
                    info.UserId = issue.User.Id;
                    await UpdateUserAsync(issue.User, cancellationToken);
                }

                if (issue.PullRequest is { } pullRequest)
                {
                    await UpdatePullRequestInfoAsync(pullRequest, info, cancellationToken);
                }

                await UpdateIssueAssigneesAsync(info, [.. issue.Assignees.Select(a => (a, a.Id, a.Login))], cancellationToken);

                if (!willAddNewComments &&
                    info.LastSemanticIngestionTime.Year < 2020 &&
                    !await DbContext.Comments.AnyAsync(c => c.IssueId == info.Id, cancellationToken))
                {
                    // This is a new issue. Check if it just got transfered, in which case we have to refetch existing comments.
                    await OnRestApiCall(1, cancellationToken);
                    IReadOnlyList<IssueEvent> events = await GitHub.Issue.Events.GetAllForIssue(RepoId, issue.Number);

                    if (events.Any(e => e.Event == EventInfoState.Transferred))
                    {
                        await OnRestApiCall(1, cancellationToken);
                        IReadOnlyList<IssueComment> issueComments = await GitHub.Issue.Comment.GetAllForIssue(RepoId, issue.Number, s_apiOptions);

                        await UpdateIssueCommentInfosAsync([.. issueComments], cancellationToken);

                        Logger.LogDebug("Refetched {Count} comments for transferred issue {IssueUrl}", issueComments.Count, issue.HtmlUrl);
                    }
                }

                Logger.LogTrace("Updated issue metadata {IssueNumber}: {IssueTitle}", issue.Number, issue.Title);
            }

            return infos;
        }

        private async Task UpdateIssueInfosAsync(GitHubGraphQL.IssueModel[] issues, IssueType issueType, CancellationToken cancellationToken)
        {
            if (issues.Length == 0)
            {
                return;
            }

            IssueInfo[] infos = await GetOrCreateIssueInfosAsync([.. issues.Select(i => (i.Id, i.Labels.Nodes.Select(l => l.Id).ToArray(), i.Milestone?.Id))], cancellationToken);

            await UpdateUserInfosAsync(
                [.. infos.Select((issue, i) => (issue, issues[i].Author?.DatabaseId ?? GhostUserId, issues[i].Author?.Login ?? "ghost"))],
                issue => issue.UserId,
                (issue, userId) => issue.UserId = userId,
                cancellationToken);

            for (int i = 0; i < infos.Length; i++)
            {
                IssueInfo info = infos[i];
                GitHubGraphQL.IssueModel issue = issues[i];

                info.IssueType = issueType;

                PopulateBasicIssueInfo(info, issue);

                await UpdateIssueAssigneesAsync(info, [.. issue.Assignees.Nodes.Select(a => ((User?)null, a.DatabaseId ?? GhostUserId, a.Login))], cancellationToken);

                info.LastObservedDuringFullRescanTime = DateTime.UtcNow;

                Logger.LogTrace("Updated issue metadata {IssueNumber}: {IssueTitle}", issue.Number, issue.Title);
            }
        }

        private async Task UpdateIssueAssigneesAsync(IssueInfo issue, (User? user, long userId, string userLogin)[] newAssignees, CancellationToken cancellationToken)
        {
            List<UserInfo> newList = [];
            List<long> usersToFetch = [];

            foreach (var (user, userId, userLogin) in newAssignees)
            {
                if (userId == GhostUserId)
                {
                    continue;
                }

                if (issue.Assignees.FirstOrDefault(a => a.Id == userId) is UserInfo userInfo)
                {
                    newList.Add(userInfo);
                }
                else
                {
                    await UpdateUserAsync(user, userId, userLogin, cancellationToken);
                    usersToFetch.Add(userId);
                }
            }

            if (usersToFetch.Count > 0)
            {
                newList.AddRange(await DbContext.Users
                    .Where(u => usersToFetch.Contains(u.Id))
                    .AsSplitQuery()
                    .ToListAsync(cancellationToken));
            }

            issue.Assignees = newList;
        }

        private async Task UpdatePullRequestInfoAsync(PullRequest pullRequest, IssueInfo issue, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pullRequest);
            ArgumentNullException.ThrowIfNull(issue);

            if (pullRequest.Id == 0)
            {
                await OnRestApiCall(1, cancellationToken);
                pullRequest = await GitHub.PullRequest.Get(RepoId, issue.Number);
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequest.Id);
            ArgumentOutOfRangeException.ThrowIfNotEqual(pullRequest.HtmlUrl, issue.HtmlUrl);

            PullRequestInfo prInfo = (await GetOrCreatePullRequestInfosAsync([pullRequest.NodeId], cancellationToken))[0];

            issue.IssueType = IssueType.PullRequest;

            prInfo.IssueId = issue.Id;
            prInfo.MergedAt = pullRequest.MergedAt?.UtcDateTime;
            prInfo.IsDraft = pullRequest.Draft;
            prInfo.Mergeable = pullRequest.MergeableState?.Value;
            prInfo.Additions = pullRequest.Additions;
            prInfo.Deletions = pullRequest.Deletions;
            prInfo.ChangedFiles = pullRequest.ChangedFiles;
            prInfo.MaintainerCanModify = pullRequest.MaintainerCanModify ?? false;

            if (pullRequest.MergedBy is not null && ShouldUpdateUserInfo(prInfo.MergedById ?? 0, pullRequest.MergedBy.Id))
            {
                prInfo.MergedById = pullRequest.MergedBy.Id;
                await UpdateUserAsync(pullRequest.MergedBy, cancellationToken);
            }
        }

        private async Task<PullRequestInfo[]> GetOrCreatePullRequestInfosAsync(string[] ids, CancellationToken cancellationToken)
        {
            Debug.Assert(ids.Length > 0);

            PullRequestInfo[] existingInfos = await DbContext.PullRequests
                .Where(pr => ids.Contains(pr.Id))
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken);

            PullRequestInfo[] results = new PullRequestInfo[ids.Length];

            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                PullRequestInfo? info = existingInfos.FirstOrDefault(pr => pr.Id == id);

                if (info is null)
                {
                    info = new PullRequestInfo
                    {
                        Id = id
                    };
                    DbContext.PullRequests.Add(info);
                }

                results[i] = info;
            }

            return results;
        }

        private async Task UpdatePullRequestInfosAsync(GitHubGraphQL.PullRequestModel[] pullRequests, CancellationToken cancellationToken)
        {
            if (pullRequests.Length == 0)
            {
                return;
            }

            PullRequestInfo[] infos = await GetOrCreatePullRequestInfosAsync([.. pullRequests.Select(pr => pr.Id)], cancellationToken);

            for (int i = 0; i < pullRequests.Length; i++)
            {
                PullRequestInfo info = infos[i];
                GitHubGraphQL.PullRequestModel pullRequest = pullRequests[i];

                info.IssueId = pullRequest.Id;
                info.MergedAt = pullRequest.MergedAt;
                info.IsDraft = pullRequest.IsDraft;
                info.Mergeable = ParseMergeableState(pullRequest.Mergeable);
                info.Additions = pullRequest.Additions;
                info.Deletions = pullRequest.Deletions;
                info.ChangedFiles = pullRequest.ChangedFiles;
                info.MaintainerCanModify = pullRequest.MaintainerCanModify;
            }
        }

        private async Task<CommentInfo[]> GetOrCreateCommentInfosAsync((string? issueId, string commentId, string commentUrl, bool isPrReviewComment)[] comments, CancellationToken cancellationToken)
        {
            Debug.Assert(_repoInfo is not null);
            Debug.Assert(comments.Length > 0);

            for (int i = 0; i < comments.Length; i++)
            {
                if (!comments[i].commentUrl.StartsWith(_repoInfo.HtmlUrl, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"Comment URL does not belong in the current repo: {comments[i].commentUrl}");
                }
            }

            string[] commentIds = [.. comments.Select(c => c.commentId)];

            List<CommentInfo> existingComments = await DbContext.Comments
                .Where(c => commentIds.Contains(c.Id))
                .AsSplitQuery()
                .ToListAsync(cancellationToken);

            CommentInfo[] results = new CommentInfo[comments.Length];

            // Copy any existing comments to the results array
            for (int i = 0; i < comments.Length; i++)
            {
                results[i] = existingComments.FirstOrDefault(c => c.Id == comments[i].commentId)!;
            }

            // For any missing comments, we need to determine their issue id
            int[] issueNumbers = comments
                .Where((c, i) => results[i] is null)
                .Where(c => c.issueId is null)
                .Select(c =>
                {
                    if (!GitHubHelper.TryParseIssueOrPRNumber(c.commentUrl, out int issueNumber))
                    {
                        throw new Exception($"Failed to parse issue number from comment URL: {c.commentUrl}");
                    }

                    return issueNumber;
                })
                .Distinct()
                .ToArray();

            Dictionary<int, string> issueNumberToId = issueNumbers.Length == 0 ? [] :
                (await DbContext.Issues
                .Where(i => i.RepositoryId == RepoId && issueNumbers.Contains(i.Number))
                .Select(i => new { i.Id, i.Number })
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken))
                .ToDictionary(i => i.Number, i => i.Id);

            for (int i = 0; i < comments.Length; i++)
            {
                results[i] ??= await CreateNewAsync(comments[i].issueId, comments[i].commentId, comments[i].commentUrl, comments[i].isPrReviewComment);
            }

            MarkUpdatedIssuesForSemanticIngestion([.. results.Select(r => r.IssueId)]);

            return results;

            async Task<CommentInfo> CreateNewAsync(string? issueId, string commentId, string commentUrl, bool isPrReviewComment)
            {
                if (issueId is null)
                {
                    bool success = GitHubHelper.TryParseIssueOrPRNumber(commentUrl, out int issueNumber);
                    Debug.Assert(success);

                    if (!issueNumberToId.TryGetValue(issueNumber, out issueId))
                    {
                        // Check if it's already being tracked in the current DbContext
                        var trackedIssue = DbContext.ChangeTracker.Entries<IssueInfo>()
                            .Where(i => i.CurrentValues.TryGetValue(nameof(IssueInfo.Number), out int num) && num == issueNumber)
                            .FirstOrDefault(i => i.CurrentValues.TryGetValue(nameof(IssueInfo.RepositoryId), out long id) && id == RepoId)
                            ?.CurrentValues;

                        if (trackedIssue is null || !trackedIssue.TryGetValue(nameof(IssueInfo.Id), out issueId))
                        {
                            // This is a comment on a previously unseen issue/pr. We need to fetch the issue/pr to get its node id.
                            // This is only relevant when polling for new comments.
                            await OnRestApiCall(1, cancellationToken);
                            Issue issue = await GitHub.Issue.Get(RepoId, issueNumber);
                            IssueInfo issueInfo = (await UpdateIssueInfosAsync([issue], willAddNewComments: true, cancellationToken))[0];
                            issueId = issueInfo.Id;
                            issueNumberToId[issue.Number] = issueId;
                        }
                    }
                }

                var newComment = new CommentInfo
                {
                    Id = commentId,
                    IssueId = issueId,
                    IsPrReviewComment = isPrReviewComment,
                };

                DbContext.Comments.Add(newComment);

                return newComment;
            }
        }

        private async Task UpdateIssueCommentInfosAsync(IssueComment[] comments, CancellationToken cancellationToken)
        {
            if (comments.Length == 0)
            {
                return;
            }

            CommentInfo[] infos = await GetOrCreateCommentInfosAsync([.. comments.Select(c => (issueId: (string?)null, c.NodeId, c.HtmlUrl, isPrReviewComment: false))], cancellationToken);

            for (int i = 0; i < comments.Length; i++)
            {
                IssueComment comment = comments[i];
                CommentInfo info = infos[i];

                info.HtmlUrl = comment.HtmlUrl;
                info.Body = comment.Body?.RemoveNullChars();
                info.GitHubIdentifier = comment.Id;
                info.CreatedAt = comment.CreatedAt.UtcDateTime;
                info.UpdatedAt = (comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime;
                info.AuthorAssociation = comment.AuthorAssociation.Value;

                PopulateCommentReactions(info, comment.Reactions);

                if (ShouldUpdateUserInfo(info.UserId, comment.User.Id))
                {
                    info.UserId = comment.User.Id;
                    await UpdateUserAsync(comment.User, cancellationToken);
                }

                Logger.LogTrace("Updated issue comment data {CommentHtmlUrl}", comment.HtmlUrl);
            }
        }

        private async Task UpdatePullRequestReviewCommentInfosAsync(PullRequestReviewComment[] comments, CancellationToken cancellationToken)
        {
            if (comments.Length == 0)
            {
                return;
            }

            CommentInfo[] infos = await GetOrCreateCommentInfosAsync([.. comments.Select(c => (issueId: (string?)null, c.NodeId, c.HtmlUrl, isPrReviewComment: true))], cancellationToken);

            for (int i = 0; i < comments.Length; i++)
            {
                PullRequestReviewComment comment = comments[i];
                CommentInfo info = infos[i];

                info.HtmlUrl = comment.HtmlUrl;
                info.Body = comment.Body?.RemoveNullChars();
                info.CreatedAt = comment.CreatedAt.UtcDateTime;
                info.UpdatedAt = (comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime;
                info.AuthorAssociation = comment.AuthorAssociation.Value;

                PopulateCommentReactions(info, comment.Reactions);

                if (comment.User is null)
                {
                    Logger.LogDebug("No user information on comment {CommentHtmlUrl}. Replacing with the Ghost user.", comment.HtmlUrl);
                    info.UserId = GhostUserId;
                }
                else if (ShouldUpdateUserInfo(info.UserId, comment.User.Id))
                {
                    info.UserId = comment.User.Id;
                    await UpdateUserAsync(comment.User, cancellationToken);
                }

                Logger.LogTrace("Updated PR review comment data {CommentHtmlUrl}", comment.HtmlUrl);
            }
        }

        private async Task UpdateCommentInfosAsync((string issueId, GitHubGraphQL.CommentModel comment, bool isPrReviewComment)[] comments, CancellationToken cancellationToken)
        {
            if (comments.Length == 0)
            {
                return;
            }

            CommentInfo[] infos = await GetOrCreateCommentInfosAsync([.. comments.Select(c => (c.issueId, c.comment.Id, c.comment.Url, c.isPrReviewComment))], cancellationToken);

            await UpdateUserInfosAsync(
                [.. infos.Select((comment, i) => (comment, comments[i].comment.Author?.DatabaseId ?? GhostUserId, comments[i].comment.Author?.Login ?? "ghost"))],
                comment => comment.UserId,
                (comment, userId) => comment.UserId = userId,
                cancellationToken);

            for (int i = 0; i < comments.Length; i++)
            {
                GitHubGraphQL.CommentModel comment = comments[i].comment;
                CommentInfo info = infos[i];

                info.HtmlUrl = comment.Url;
                info.Body = comment.Body?.RemoveNullChars();
                info.GitHubIdentifier = comment.DatabaseId;
                info.CreatedAt = comment.CreatedAt;
                info.UpdatedAt = comment.UpdatedAt;
                info.AuthorAssociation = ParseAuthorAssociation(comment.AuthorAssociation);
                info.IsMinimized = comment.IsMinimized;
                info.MinimizedReason = comment.MinimizedReason;

                PopulateCommentReactions(info, ParseReactions(comment.ReactionGroups));

                info.LastObservedDuringFullRescanTime = DateTime.UtcNow;

                Logger.LogTrace("Updated issue comment data {CommentHtmlUrl}", comment.Url);
            }
        }

        private void MarkUpdatedIssuesForSemanticIngestion(string[] issueIds)
        {
            foreach (string id in issueIds)
            {
                if (_issuesMarkedForSemanticIngestion.Add(id))
                {
                    DbContext.SemanticIngestionBacklog.Add(new SemanticIngestionBacklogEntry { IssueId = id });
                }
            }
        }

        private static void PopulateCommentReactions(CommentInfo info, ReactionSummary reactions)
        {
            info.Plus1 = reactions.Plus1;
            info.Minus1 = reactions.Minus1;
            info.Laugh = reactions.Laugh;
            info.Confused = reactions.Confused;
            info.Eyes = reactions.Eyes;
            info.Heart = reactions.Heart;
            info.Hooray = reactions.Hooray;
            info.Rocket = reactions.Rocket;
        }

        private static ReactionSummary ParseReactions(GitHubGraphQL.ReactionGroupModel[] reactions)
        {
            int thumbsUp = 0;
            int thumbsDown = 0;
            int laugh = 0;
            int confused = 0;
            int heart = 0;
            int hooray = 0;
            int rocket = 0;
            int eyes = 0;

            foreach (GitHubGraphQL.ReactionGroupModel reaction in reactions)
            {
                switch (reaction.Content)
                {
                    case "THUMBS_UP":
                        thumbsUp = reaction.Reactors.TotalCount;
                        break;
                    case "THUMBS_DOWN":
                        thumbsDown = reaction.Reactors.TotalCount;
                        break;
                    case "LAUGH":
                        laugh = reaction.Reactors.TotalCount;
                        break;
                    case "CONFUSED":
                        confused = reaction.Reactors.TotalCount;
                        break;
                    case "HEART":
                        heart = reaction.Reactors.TotalCount;
                        break;
                    case "HOORAY":
                        hooray = reaction.Reactors.TotalCount;
                        break;
                    case "ROCKET":
                        rocket = reaction.Reactors.TotalCount;
                        break;
                    case "EYES":
                        eyes = reaction.Reactors.TotalCount;
                        break;

                    default:
                        break;
                }
            }

            return new ReactionSummary(0, thumbsUp, thumbsDown, laugh, confused, heart, hooray, eyes, rocket, string.Empty);
        }

        private static ItemState ParseItemState(string state)
        {
            if ("OPEN".Equals(state, StringComparison.OrdinalIgnoreCase)) return ItemState.Open;
            if ("CLOSED".Equals(state, StringComparison.OrdinalIgnoreCase)) return ItemState.Closed;
            if ("MERGED".Equals(state, StringComparison.OrdinalIgnoreCase)) return ItemState.Closed;
            return default;
        }

        private static LockReason ParseLockReason(string reason)
        {
            if ("RESOLVED".Equals(reason, StringComparison.OrdinalIgnoreCase)) return LockReason.Resolved;
            if ("OFF_TOPIC".Equals(reason, StringComparison.OrdinalIgnoreCase)) return LockReason.OffTopic;
            if ("SPAM".Equals(reason, StringComparison.OrdinalIgnoreCase)) return LockReason.Spam;
            if ("TOO_HEATED".Equals(reason, StringComparison.OrdinalIgnoreCase)) return LockReason.TooHeated;
            return default;
        }

        private static AuthorAssociation ParseAuthorAssociation(string association)
        {
            if ("COLLABORATOR".Equals(association, StringComparison.OrdinalIgnoreCase)) return AuthorAssociation.Collaborator;
            if ("CONTRIBUTOR".Equals(association, StringComparison.OrdinalIgnoreCase)) return AuthorAssociation.Contributor;
            if ("FIRST_TIMER".Equals(association, StringComparison.OrdinalIgnoreCase)) return AuthorAssociation.FirstTimer;
            if ("FIRST_TIME_CONTRIBUTOR".Equals(association, StringComparison.OrdinalIgnoreCase)) return AuthorAssociation.FirstTimeContributor;
            if ("MEMBER".Equals(association, StringComparison.OrdinalIgnoreCase)) return AuthorAssociation.Member;
            if ("NONE".Equals(association, StringComparison.OrdinalIgnoreCase)) return AuthorAssociation.None;
            if ("OWNER".Equals(association, StringComparison.OrdinalIgnoreCase)) return AuthorAssociation.Owner;
            return default;
        }

        private static MergeableState ParseMergeableState(string state)
        {
            if ("CONFLICTING".Equals(state, StringComparison.OrdinalIgnoreCase)) return MergeableState.Dirty;
            if ("MERGEABLE".Equals(state, StringComparison.OrdinalIgnoreCase)) return MergeableState.Clean;
            if ("UNKNOWN".Equals(state, StringComparison.OrdinalIgnoreCase)) return MergeableState.Unknown;
            return default;
        }
    }
}
