using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class GitHubDataService : IHostedService
{
    public const int GhostUserId = 10137;
    public const int CopilotUserId = 198982749;

    private static readonly TimeSpan UserInfoRefreshInterval = TimeSpan.FromDays(50);
    private static readonly TimeSpan RepositoryInfoRefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DataPollingOffset = TimeSpan.FromSeconds(10);

    private readonly (string Owner, string Name, TimeSpan IssueUpdateFrequency, TimeSpan CommentUpdateFrequency)[] _watchedRepos =
    [
        ("dotnet", "runtime",           TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15)),
        ("dotnet", "yarp",              TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
        ("dotnet", "aspnetcore",        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
        ("dotnet", "extensions",        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
        ("dotnet", "aspire",            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
        ("dotnet", "sdk",               TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
        ("dotnet", "roslyn",            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
        ("dotnet", "efcore",            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)),
        ("dotnet", "BenchmarkDotNet",   TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)),
        ("dotnet", "interactive",       TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)),
    ];

    public string[] WatchedRepos => field ??= [.. _watchedRepos.Select(r => $"{r.Owner}/{r.Name}").Order()];

    private readonly GitHubClient _github;
    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly Logger _logger;
    private readonly ServiceConfiguration _serviceConfiguration;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly ConcurrentDictionary<(string Owner, string Name), long> _repositoryIds = [];
    private readonly CooldownTracker _rateLimit = new(TimeSpan.FromHours(1) / 4000, cooldownTolerance: 50, adminOverride: false);
    private readonly ConcurrentDictionary<string, long> _repoNameToId = new();
    private Task _updatesTask;

    public int IssueCount { get; private set; }
    public int CommentCount { get; private set; }
    public int SearchVectorCount { get; private set; }

    public GitHubDataService(GitHubClient gitHub, IDbContextFactory<GitHubDbContext> db, Logger logger, ServiceConfiguration serviceConfiguration)
    {
        _github = gitHub;
        _db = db;
        _logger = logger;
        _serviceConfiguration = serviceConfiguration;
    }

    private async Task RefreshStatsAsync()
    {
        await using GitHubDbContext dbContext = _db.CreateDbContext();

        IssueCount = await dbContext.Issues.AsNoTracking().CountAsync();
        CommentCount = await dbContext.Comments.AsNoTracking().CountAsync();
        SearchVectorCount = await dbContext.IngestedEmbeddings.AsNoTracking().CountAsync();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            using AsyncFlowControl _ = ExecutionContext.SuppressFlow();

            _updatesTask = Task.Run(async () => await RunUpdateLoopAsync(cancellationToken), CancellationToken.None);
        }
        else
        {
            await RefreshStatsAsync();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _updateCts.CancelAsync();

        if (_updatesTask is not null)
        {
            await _updatesTask.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _updateCts.Token);
            cancellationToken = linkedCts.Token;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

            int consecutiveFailureCount = 0;

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (_serviceConfiguration.PauseGitHubPolling)
                    {
                        continue;
                    }

                    foreach ((string repoOwner, string repoName, TimeSpan issueUpdateFrequency, TimeSpan commentUpdateFrequency) in _watchedRepos)
                    {
                        (int apiCalls, int updates) = await UpdateRepositoryDataAsync(repoOwner, repoName, issueUpdateFrequency, commentUpdateFrequency);
                    }

                    consecutiveFailureCount = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailureCount++;

                    string errorMessage = $"{nameof(GitHubDataService)}: Update failed ({consecutiveFailureCount}): {ex}";
                    _logger.DebugLog(errorMessage);

                    await Task.Delay(TimeSpan.FromMinutes(2) * consecutiveFailureCount, cancellationToken);

                    if (consecutiveFailureCount == 4) // 20 min
                    {
                        await _logger.DebugAsync(errorMessage);
                    }

                    if (ex is RateLimitExceededException rateLimitEx)
                    {
                        TimeSpan toWait = rateLimitEx.GetRetryAfterTimeSpan();
                        if (toWait.TotalSeconds > 1)
                        {
                            _logger.DebugLog($"GitHub polling toWait={toWait}");
                            if (toWait > TimeSpan.FromMinutes(5))
                            {
                                toWait = TimeSpan.FromMinutes(5);
                            }
                            await Task.Delay(toWait, cancellationToken);
                        }
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

    public async Task<(int ApiCalls, int Updates)> UpdateRepositoryDataAsync(string repoOwner, string repoName, TimeSpan issueUpdateFrequency, TimeSpan commentspdateFrequency, int targetApiRatePerHour = -1, GitHubClient alternativeClient = null)
    {
        if (OperatingSystem.IsWindows())
        {
            issueUpdateFrequency *= 2;
            commentspdateFrequency *= 2;
        }

        await using GitHubDbContext dbContext = _db.CreateDbContext();

        var context = new UpdateContext
        {
            Parent = this,
            DbContext = dbContext,
            GitHub = alternativeClient,
        };

        if (targetApiRatePerHour > 0)
        {
            context.RateLimit = new CooldownTracker(TimeSpan.FromHours(1) / targetApiRatePerHour, cooldownTolerance: 2, adminOverride: false);
        }

        await context.UpdateRepositoryDataAsync(repoOwner, repoName, issueUpdateFrequency, commentspdateFrequency);

        return (context.ApiCallsPerformed, context.UpdatesPerformed);
    }

    public async IAsyncEnumerable<(string Log, int ApiCalls, int DbUpdates)> IngestNewRepositoryAsync(string repoOwner, string repoName, int initialIssueNumber, int targetApiRatePerHour, GitHubClient alternativeClient, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetApiRatePerHour);

        await using GitHubDbContext dbContext = _db.CreateDbContext();

        var context = new UpdateContext
        {
            Parent = this,
            DbContext = dbContext,
            GitHub = alternativeClient,
        };

        if (targetApiRatePerHour > 0)
        {
            context.RateLimit = new CooldownTracker(TimeSpan.FromHours(1) / targetApiRatePerHour, cooldownTolerance: 2, adminOverride: false);
        }

        await foreach (string update in context.IngestNewRepositoryAsync(repoOwner, repoName, initialIssueNumber, cancellationToken))
        {
            yield return (update, context.ApiCallsPerformed, context.UpdatesPerformed);
        }
    }

    public static void PopulateBasicIssueInfo(IssueInfo info, Issue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(info.RepositoryId);

        info.Id = CreateId(info.RepositoryId, issue.Id, ResourceTypes.Issue);
        info.GitHubIdentifier = issue.Id;
        info.NodeIdentifier = issue.NodeId;
        info.HtmlUrl = issue.HtmlUrl;
        info.Number = issue.Number;
        info.Title = issue.Title;
        info.Body = issue.Body;
        info.State = issue.State.Value;
        info.CreatedAt = issue.CreatedAt.UtcDateTime;
        info.UpdatedAt = (issue.UpdatedAt ?? issue.CreatedAt).UtcDateTime;
        info.ClosedAt = issue.ClosedAt?.UtcDateTime;
        info.Locked = issue.Locked;
        info.ActiveLockReason = issue.ActiveLockReason?.Value;

        info.Plus1 = issue.Reactions.Plus1;
        info.Minus1 = issue.Reactions.Minus1;
        info.Laugh = issue.Reactions.Laugh;
        info.Confused = issue.Reactions.Confused;
        info.Eyes = issue.Reactions.Eyes;
        info.Heart = issue.Reactions.Heart;
        info.Hooray = issue.Reactions.Hooray;
        info.Rocket = issue.Reactions.Rocket;
    }

    public async Task<long> TryGetKnownRepositoryIdAsync(string repoName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(repoName))
        {
            return -1;
        }

        if (_repoNameToId.TryGetValue(repoName, out long id))
        {
            return id;
        }

        await using GitHubDbContext db = _db.CreateDbContext();

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

    public async Task<RepositoryInfo> TryGetRepositoryInfoAsync(string repoName)
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

    private static class ResourceTypes
    {
        public const string Issue = "i";
        public const string PullRequest = "pr";
        public const string IssueComment = "ic";
        public const string PullRequestReviewComment = "prrc";
        public const string Label = "l";
    }

    private static string CreateId(long repoId, long resourceId, string resourceType)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repoId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);

        return $"{repoId}/{resourceId}/{resourceType}";
    }

    private sealed class UpdateContext
    {
        private static readonly ApiOptions s_apiOptions = new()
        {
            PageSize = 100,
            PageCount = 50,
        };

        public required GitHubDataService Parent { get; init; }
        public required GitHubDbContext DbContext { get; init; }

        public CooldownTracker RateLimit { get => field ?? Parent._rateLimit; set => field = value; }
        public GitHubClient GitHub { get => field ?? Parent._github; set; }
        private Logger Logger => Parent._logger;

        public int ApiCallsPerformed { get; private set; }
        public int UpdatesPerformed { get; private set; }

        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly HashSet<long> _updatedUsers = [];
        private readonly HashSet<long> _updatedPRsByIssueId = [];
        private readonly Dictionary<int, long> _issueNumberToId = [];
        private long _repoId;
        private RepositoryInfo _repoInfo;

        private async Task OnApiCall(int count = 1, CancellationToken cancellationToken = default, [CallerMemberName] string caller = null)
        {
            for (int i = 0; i < count; i++)
            {
                ApiCallsPerformed++;

                while (!RateLimit.TryEnter(0, out TimeSpan cooldown, out _))
                {
                    cooldown += TimeSpan.FromMilliseconds(50);

                    Log($"{nameof(GitHubDataService)}: {_repoInfo?.FullName} {caller} Performed {ApiCallsPerformed} API calls, {UpdatesPerformed} DB updates, sleeping for {cooldown.TotalSeconds:N1} seconds", verbose: true);

                    await Task.Delay(cooldown, cancellationToken);
                }
            }
        }

        private async Task InitRepositoryInfo(string repoOwner, string repoName)
        {
            if (!Parent._repositoryIds.TryGetValue((repoOwner, repoName), out _repoId))
            {
                await OnApiCall();
                Repository repo = await GitHub.Repository.Get(repoOwner, repoName);
                _repoId = repo.Id;
                Parent._repositoryIds[(repoOwner, repoName)] = repo.Id;
            }

            _repoInfo = await GetOrUpdateRepositoryInfoAsync();
            Debug.Assert(_repoId == _repoInfo.Id);

            UpdatesPerformed += await DbContext.SaveChangesAsync(CancellationToken.None);
        }

        public async Task UpdateRepositoryDataAsync(string repoOwner, string repoName, TimeSpan issueUpdateFrequency, TimeSpan commentspdateFrequency)
        {
            await InitRepositoryInfo(repoOwner, repoName);

            if (_repoInfo.Archived)
            {
                return;
            }

            await Parent.RefreshStatsAsync();

            DateTime startTime = DateTime.UtcNow;
            if (startTime - _repoInfo.LastIssuesUpdate >= issueUpdateFrequency)
            {
                IReadOnlyList<Issue> issues = await GitHub.Issue.GetAllForRepository(_repoId, new RepositoryIssueRequest
                {
                    Since = _repoInfo.LastIssuesUpdate - DataPollingOffset,
                    SortProperty = IssueSort.Updated,
                    SortDirection = SortDirection.Descending,
                    Filter = IssueFilter.All,
                    State = ItemStateFilter.All,
                }, s_apiOptions);

                Log($"Found {issues.Count} updated issues since {_repoInfo.LastIssuesUpdate.ToISODateTime()}", verbose: true);

                await OnApiCall((issues.Count / s_apiOptions.PageSize.Value) + 1);

                foreach (Issue issue in issues)
                {
                    await UpdateIssueInfoAsync(issue);
                }

                _repoInfo.LastIssuesUpdate = DeduceLastUpdateTime(issues.Select(i => (i.UpdatedAt ?? i.CreatedAt).UtcDateTime).ToList());
                UpdatesPerformed += await DbContext.SaveChangesAsync(CancellationToken.None);
            }

            startTime = DateTime.UtcNow;
            if (startTime - _repoInfo.LastIssueCommentsUpdate >= commentspdateFrequency)
            {
                IReadOnlyList<IssueComment> issueComments = await GitHub.Issue.Comment.GetAllForRepository(_repoId, new IssueCommentRequest
                {
                    Since = _repoInfo.LastIssueCommentsUpdate - DataPollingOffset,
                    Sort = IssueCommentSort.Updated,
                    Direction = SortDirection.Descending,
                }, s_apiOptions);

                Log($"Found {issueComments.Count} updated issue comments since {_repoInfo.LastIssueCommentsUpdate.ToISODateTime()}", verbose: true);

                await OnApiCall((issueComments.Count / s_apiOptions.PageSize.Value) + 1);

                foreach (IssueComment comment in issueComments)
                {
                    await UpdateIssueCommentInfoAsync(comment);
                }

                _repoInfo.LastIssueCommentsUpdate = DeduceLastUpdateTime(issueComments.Select(comment => (comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime).ToList());
                UpdatesPerformed += await DbContext.SaveChangesAsync(CancellationToken.None);
            }

            startTime = DateTime.UtcNow;
            if (startTime - _repoInfo.LastPullRequestReviewCommentsUpdate >= commentspdateFrequency)
            {
                IReadOnlyList<PullRequestReviewComment> prReviewComments = await GitHub.PullRequest.ReviewComment.GetAllForRepository(_repoId, new PullRequestReviewCommentRequest
                {
                    Since = _repoInfo.LastPullRequestReviewCommentsUpdate - DataPollingOffset,
                    Sort = PullRequestReviewCommentSort.Updated,
                    Direction = SortDirection.Descending,
                }, s_apiOptions);

                Log($"Found {prReviewComments.Count} updated PR review comments since {_repoInfo.LastPullRequestReviewCommentsUpdate.ToISODateTime()}", verbose: true);

                await OnApiCall((prReviewComments.Count / s_apiOptions.PageSize.Value) + 1);

                foreach (PullRequestReviewComment comment in prReviewComments)
                {
                    await UpdatePullRequestReviewCommentInfoAsync(comment);
                }

                _repoInfo.LastPullRequestReviewCommentsUpdate = DeduceLastUpdateTime(prReviewComments.Select(comment => (comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime).ToList());
            }

            UpdatesPerformed += await DbContext.SaveChangesAsync(CancellationToken.None);
            Log($"Finished updating repository. {ApiCallsPerformed} API calls, {UpdatesPerformed} DB updates.", verbose: true);
        }

        public async IAsyncEnumerable<string> IngestNewRepositoryAsync(string repoOwner, string repoName, int initialIssueNumber, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await InitRepositoryInfo(repoOwner, repoName);

            int lastIssueNumber = (await GitHub.Issue.GetAllForRepository(_repoId, new RepositoryIssueRequest
            {
                SortProperty = IssueSort.Created,
                SortDirection = SortDirection.Descending,
                Filter = IssueFilter.All,
                State = ItemStateFilter.All,
            }, new ApiOptions { PageCount = 1, PageSize = 1 })).Single().Number;

            HashSet<int> existingIssueNumbers = await DbContext.Issues
                .AsNoTracking()
                .Where(i => i.RepositoryId == _repoId)
                .Select(i => i.Number)
                .ToHashSetAsync(cancellationToken);

            if (initialIssueNumber < 0)
            {
                initialIssueNumber = existingIssueNumbers.Max();
            }

            for (int issueNumber = Math.Max(initialIssueNumber - 1, 1); issueNumber <= lastIssueNumber; issueNumber++)
            {
                yield return $"Processing issue #{issueNumber} out of {lastIssueNumber}";

                if (!existingIssueNumbers.Add(issueNumber))
                {
                    continue;
                }

                Issue issue = null;
                try
                {
                    await OnApiCall(1, cancellationToken);
                    issue = await GitHub.Issue.Get(_repoId, issueNumber);
                }
                catch { }

                if (issue is null || issue.HtmlUrl is null)
                {
                    yield return $"Failed to fetch issue #{issueNumber}. It may have been deleted/transferred.";
                    continue;
                }

                if (!issue.HtmlUrl.StartsWith(_repoInfo.HtmlUrl, StringComparison.Ordinal) ||
                    issue.HtmlUrl[_repoInfo.HtmlUrl.Length] != '/') // e.g. dotnet/runtime and dotnet/runtimelab
                {
                    yield return $"Issue #{issueNumber} was transferred to <{issue.HtmlUrl}>.";
                    continue;
                }

                await UpdateIssueInfoAsync(issue);

                IReadOnlyList<IssueComment> issueComments = await GitHub.Issue.Comment.GetAllForIssue(_repoId, issue.Number, s_apiOptions);

                await OnApiCall((issueComments.Count / s_apiOptions.PageSize.Value) + 1, cancellationToken);

                foreach (IssueComment comment in issueComments)
                {
                    await UpdateIssueCommentInfoAsync(comment);
                }

                if (issue.PullRequest is not null)
                {
                    IReadOnlyList<PullRequestReviewComment> prReviewComments = await GitHub.PullRequest.ReviewComment.GetAll(_repoId, issue.Number, s_apiOptions);

                    await OnApiCall((prReviewComments.Count / s_apiOptions.PageSize.Value) + 1, cancellationToken);

                    foreach (PullRequestReviewComment comment in prReviewComments)
                    {
                        await UpdatePullRequestReviewCommentInfoAsync(comment);
                    }
                }

                UpdatesPerformed += await DbContext.SaveChangesAsync(CancellationToken.None);
            }
        }

        private void Log(string message, bool verbose = false)
        {
            if (_repoInfo is null)
            {
                return;
            }

            message = $"{nameof(GitHubDataService)}: {_repoInfo.Owner.Login}/{_repoInfo.Name} {message}";

            if (verbose)
            {
                Logger.TraceLog(message);
            }
            else
            {
                Logger.DebugLog(message);
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

            Log($"Count={dates.Count} Min={min} Max={max} Start={_startTime}", verbose: true);

            // Recent update. Next request will fetch everything anyway.
            if (dates.Count < s_apiOptions.PageSize.Value / 2)
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

        private static bool ShouldUpdateUserInfo(long currentUserId, User newUser)
        {
            ArgumentNullException.ThrowIfNull(newUser);

            return
                currentUserId == 0 ||
                currentUserId == newUser.Id ||
                newUser.Id != GhostUserId;
        }

        private async Task<RepositoryInfo> GetOrUpdateRepositoryInfoAsync()
        {
            RepositoryInfo info = await DbContext.Repositories
                .Where(r => r.Id == _repoId)
                .Include(r => r.Owner)
                .Include(r => r.Labels)
                .AsSplitQuery()
                .SingleOrDefaultAsync(CancellationToken.None);

            if (info is not null && DateTime.UtcNow - info.LastRepositoryMetadataUpdate < RepositoryInfoRefreshInterval)
            {
                return info;
            }

            await OnApiCall();
            Repository repo = await GitHub.Repository.Get(_repoId);

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
            info.Name = repo.Name;
            info.FullName = repo.FullName;
            info.Description = repo.Description;
            info.CreatedAt = repo.CreatedAt.UtcDateTime;
            info.UpdatedAt = repo.UpdatedAt.UtcDateTime;
            info.Private = repo.Private;
            info.Archived = repo.Archived;
            info.LastRepositoryMetadataUpdate = DateTime.UtcNow;
            info.LastIssuesUpdate = info.LastIssuesUpdate == default ? repo.CreatedAt.UtcDateTime : info.LastIssuesUpdate;
            info.LastIssueCommentsUpdate = info.LastIssueCommentsUpdate == default ? repo.CreatedAt.UtcDateTime : info.LastIssueCommentsUpdate;
            info.LastPullRequestReviewCommentsUpdate = info.LastPullRequestReviewCommentsUpdate == default ? repo.CreatedAt.UtcDateTime : info.LastPullRequestReviewCommentsUpdate;

            if (ShouldUpdateUserInfo(info.OwnerId, repo.Owner))
            {
                info.OwnerId = repo.Owner.Id;
                await UpdateUserAsync(repo.Owner);
            }

            await UpdateRepositoryLabelsAsync(info);

            return info;
        }

        private async Task UpdateRepositoryLabelsAsync(RepositoryInfo info)
        {
            await OnApiCall();
            IReadOnlyList<Label> labels = await GitHub.Issue.Labels.GetAllForRepository(_repoId, s_apiOptions);

            foreach (Label label in labels)
            {
                string labelId = CreateId(_repoId, label.Id, ResourceTypes.Label);

                LabelInfo labelInfo = await DbContext.Labels.FirstOrDefaultAsync(l => l.Id == labelId, CancellationToken.None);

                if (labelInfo is null)
                {
                    labelInfo = new LabelInfo
                    {
                        Id = labelId
                    };
                    DbContext.Labels.Add(labelInfo);
                }

                labelInfo.Id = labelId;
                labelInfo.GitHubIdentifier = label.Id;
                labelInfo.NodeIdentifier = label.NodeId;
                labelInfo.Url = label.Url;
                labelInfo.Name = label.Name;
                labelInfo.Color = label.Color;
                labelInfo.Description = label.Description;
                labelInfo.RepositoryId = _repoId;
            }

            info.Labels = await DbContext.Labels
                .Where(l => l.RepositoryId == _repoId)
                .ToListAsync(CancellationToken.None);
        }

        private async Task<IssueInfo> UpdateIssueInfoAsync(Issue issue)
        {
            _issueNumberToId[issue.Number] = issue.Id;

            string issueId = CreateId(_repoId, issue.Id, ResourceTypes.Issue);

            IssueInfo info = await DbContext.Issues
                .Where(i => i.Id == issueId)
                .Include(i => i.Labels)
                .AsSplitQuery()
                .SingleOrDefaultAsync(CancellationToken.None);

            if (info is not null && info.Body != issue.Body)
            {
                DbContext.BodyEditHistory.Add(new BodyEditHistoryEntry
                {
                    ResourceIdentifier = issueId,
                    UpdatedAt = issue.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow,
                    PreviousBody = info.Body,
                    IsComment = false
                });
            }

            if (info is null)
            {
                info = new IssueInfo
                {
                    Id = issueId
                };
                DbContext.Issues.Add(info);
            }

            info.RepositoryId = _repoId;

            if (issue.Labels.Any(il => !_repoInfo.Labels.Any(rl => il.Id == rl.GitHubIdentifier)))
            {
                await UpdateRepositoryLabelsAsync(_repoInfo);
            }

            info.Labels = [.. _repoInfo.Labels.Where(rl => issue.Labels.Any(il => il.Id == rl.GitHubIdentifier))];

            PopulateBasicIssueInfo(info, issue);

            if (ShouldUpdateUserInfo(info.UserId, issue.User))
            {
                info.UserId = issue.User.Id;
                await UpdateUserAsync(issue.User);
            }

            if (issue.PullRequest is { } pullRequest)
            {
                await UpdatePullRequestInfoAsync(pullRequest, info);
            }

            Log($"Updated issue metadata {issue.Number}: {issue.Title}", verbose: true);

            return info;
        }

        private async Task UpdatePullRequestInfoAsync(PullRequest pullRequest, IssueInfo issue)
        {
            ArgumentNullException.ThrowIfNull(pullRequest);
            ArgumentNullException.ThrowIfNull(issue);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issue.GitHubIdentifier);

            if (!_updatedPRsByIssueId.Add(issue.GitHubIdentifier))
            {
                return;
            }

            if (pullRequest.Id == 0)
            {
                await OnApiCall();
                pullRequest = await GitHub.PullRequest.Get(_repoId, issue.Number);
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequest.Id);
            ArgumentOutOfRangeException.ThrowIfNotEqual(pullRequest.HtmlUrl, issue.HtmlUrl);

            string pullRequestId = CreateId(_repoId, pullRequest.Id, ResourceTypes.PullRequest);

            PullRequestInfo prInfo = await DbContext.PullRequests.SingleOrDefaultAsync(i => i.Id == pullRequestId, CancellationToken.None);

            if (prInfo is null)
            {
                prInfo = new PullRequestInfo
                {
                    Id = pullRequestId
                };
                DbContext.PullRequests.Add(prInfo);
            }

            prInfo.Id = pullRequestId;
            prInfo.GitHubIdentifier = pullRequest.Id;
            prInfo.IssueId = issue.Id;
            prInfo.NodeIdentifier = pullRequest.NodeId;
            prInfo.MergedAt = pullRequest.MergedAt?.UtcDateTime;
            prInfo.Draft = pullRequest.Draft;
            prInfo.Mergeable = pullRequest.Mergeable;
            prInfo.MergeableState = pullRequest.MergeableState?.Value;
            prInfo.MergeCommitSha = pullRequest.MergeCommitSha;
            prInfo.Commits = pullRequest.Commits;
            prInfo.Additions = pullRequest.Additions;
            prInfo.Deletions = pullRequest.Deletions;
            prInfo.ChangedFiles = pullRequest.ChangedFiles;
            prInfo.MaintainerCanModify = pullRequest.MaintainerCanModify;

            if (pullRequest.MergedBy is not null && ShouldUpdateUserInfo(prInfo.MergedById ?? 0, pullRequest.MergedBy))
            {
                prInfo.MergedById = pullRequest.MergedBy.Id;
                await UpdateUserAsync(pullRequest.MergedBy);
            }
        }

        private async Task<CommentInfo> GetOrCreateCommentInfoAsync(long gitHubCommentId, string commentUrl, DateTime updatedAt, string newCommentBody, bool isPrReviewComment)
        {
            string commentId = CreateId(_repoId, gitHubCommentId, isPrReviewComment ? ResourceTypes.PullRequestReviewComment : ResourceTypes.IssueComment);

            if (await DbContext.Comments.SingleOrDefaultAsync(c => c.Id == commentId, CancellationToken.None) is { } existingComment)
            {
                if (commentUrl != existingComment.HtmlUrl)
                {
                    throw new InvalidOperationException($"Comment URL mismatch: {commentUrl} -- {existingComment.HtmlUrl}");
                }

                if (updatedAt < existingComment.UpdatedAt)
                {
                    // Ignore stale update
                    return existingComment;
                }

                if (existingComment.Body != newCommentBody)
                {
                    DbContext.BodyEditHistory.Add(new BodyEditHistoryEntry
                    {
                        ResourceIdentifier = existingComment.Id,
                        UpdatedAt = updatedAt,
                        PreviousBody = existingComment.Body,
                        IsComment = true
                    });
                }

                return existingComment;
            }

            if (!GitHubHelper.TryParseIssueOrPRNumber(commentUrl, out int issueNumber))
            {
                throw new Exception($"Failed to parse issue number from comment URL: {commentUrl}");
            }

            if (!_issueNumberToId.TryGetValue(issueNumber, out long issueGitHubId))
            {
                IssueInfo issueInfo = await DbContext.Issues.SingleOrDefaultAsync(issue => issue.Number == issueNumber && issue.RepositoryId == _repoId, CancellationToken.None);

                if (issueInfo is null)
                {
                    await OnApiCall();
                    Issue issue = await GitHub.Issue.Get(_repoId, issueNumber);
                    issueInfo = await UpdateIssueInfoAsync(issue);
                }

                issueGitHubId = issueInfo.GitHubIdentifier;
            }

            _issueNumberToId[issueNumber] = issueGitHubId;

            var newComment = new CommentInfo
            {
                Id = commentId,
                GitHubIdentifier = gitHubCommentId,
                IssueId = CreateId(_repoId, issueGitHubId, ResourceTypes.Issue),
                IsPrReviewComment = isPrReviewComment,
            };

            DbContext.Comments.Add(newComment);

            return newComment;
        }

        private async Task UpdateIssueCommentInfoAsync(IssueComment comment)
        {
            CommentInfo info = await GetOrCreateCommentInfoAsync(comment.Id, comment.HtmlUrl, comment.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow, comment.Body, isPrReviewComment: false);

            info.NodeIdentifier = comment.NodeId;
            info.HtmlUrl = comment.HtmlUrl;
            info.Body = comment.Body;
            info.CreatedAt = comment.CreatedAt.UtcDateTime;
            info.UpdatedAt = (comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime;
            info.AuthorAssociation = comment.AuthorAssociation.Value;

            info.Plus1 = comment.Reactions.Plus1;
            info.Minus1 = comment.Reactions.Minus1;
            info.Laugh = comment.Reactions.Laugh;
            info.Confused = comment.Reactions.Confused;
            info.Eyes = comment.Reactions.Eyes;
            info.Heart = comment.Reactions.Heart;
            info.Hooray = comment.Reactions.Hooray;
            info.Rocket = comment.Reactions.Rocket;

            if (ShouldUpdateUserInfo(info.UserId, comment.User))
            {
                info.UserId = comment.User.Id;
                await UpdateUserAsync(comment.User);
            }

            Log($"Updated issue comment data {comment.HtmlUrl}", verbose: true);
        }

        private async Task UpdatePullRequestReviewCommentInfoAsync(PullRequestReviewComment comment)
        {
            DateTime updatedAt = (comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime;

            CommentInfo info = await GetOrCreateCommentInfoAsync(comment.Id, comment.HtmlUrl, updatedAt, comment.Body, isPrReviewComment: true);

            info.NodeIdentifier = comment.NodeId;
            info.HtmlUrl = comment.HtmlUrl;
            info.Body = comment.Body;
            info.CreatedAt = comment.CreatedAt.UtcDateTime;
            info.UpdatedAt = updatedAt;
            info.AuthorAssociation = comment.AuthorAssociation.Value;

            info.Plus1 = comment.Reactions.Plus1;
            info.Minus1 = comment.Reactions.Minus1;
            info.Laugh = comment.Reactions.Laugh;
            info.Confused = comment.Reactions.Confused;
            info.Eyes = comment.Reactions.Eyes;
            info.Heart = comment.Reactions.Heart;
            info.Hooray = comment.Reactions.Hooray;
            info.Rocket = comment.Reactions.Rocket;

            if (comment.User is null)
            {
                Log($"No user information on comment {comment.HtmlUrl}. Replacing with the Ghost user.");
                info.UserId = GhostUserId;
            }
            else if (ShouldUpdateUserInfo(info.UserId, comment.User))
            {
                info.UserId = comment.User.Id;
                await UpdateUserAsync(comment.User);
            }

            Log($"Updated PR review comment data {comment.HtmlUrl}", verbose: true);
        }

        private async Task UpdateUserAsync(User user)
        {
            if (!_updatedUsers.Add(user.Id))
            {
                return;
            }

            UserInfo info = await DbContext.Users.SingleOrDefaultAsync(u => u.Id == user.Id, CancellationToken.None);

            if (info is not null && DateTime.UtcNow - info.EntryUpdatedAt < UserInfoRefreshInterval)
            {
                return;
            }

            try
            {
                if (user.Id is not (GhostUserId or CopilotUserId))
                {
                    await OnApiCall();
                    user = await GitHub.User.Get(user.Login);
                }
            }
            catch (NotFoundException)
            {
                Log($"User {user.Id} {user.Login} not found.");

                if (info is not null)
                {
                    // We already have some info about this user.
                    info.EntryUpdatedAt = DateTime.UtcNow;
                    return;
                }
            }

            if (info is null)
            {
                info = new UserInfo
                {
                    Id = user.Id
                };
                DbContext.Users.Add(info);
            }

            info.Id = user.Id;
            info.NodeIdentifier = user.NodeId;
            info.Login = user.Login;
            info.Name = user.Name;
            info.HtmlUrl = user.HtmlUrl;
            info.Followers = user.Followers;
            info.Following = user.Following;
            info.Company = user.Company;
            info.Location = user.Location;
            info.Bio = user.Bio;
            info.Type = user.Type;
            info.CreatedAt = user.CreatedAt.UtcDateTime;
            info.EntryUpdatedAt = DateTime.UtcNow;

            Log($"Updated user metadata {user.Id}: {user.Login} ({user.Name})", verbose: true);
        }
    }
}
