using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;
using Octokit;
using Org.BouncyCastle.Ocsp;

namespace MihuBot.RuntimeUtils;

public sealed class GitHubDataService : IHostedService
{
    private const int GhostUserId = 10137;

    private static readonly TimeSpan UserInfoRefreshInterval = TimeSpan.FromDays(5);
    private static readonly TimeSpan RepositoryInfoRefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DataPollingOffset = TimeSpan.FromSeconds(10);

    private readonly (string Owner, string Name, TimeSpan IssueUpdateFrequency, TimeSpan CommentUpdateFrequency)[] _watchedRepos =
    [
        ("dotnet", "runtime", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15))
        // ("dotnet", "yarp", TimeSpan.FromHours(1), TimeSpan.FromSeconds(60))
    ];

    private readonly GitHubClient _github;
    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly Dictionary<(string Owner, string Name), long> _repositoryIds = [];
    private Task _updatesTask;

    public int IssueCount { get; private set; }
    public int CommentCount { get; private set; }
    public int SearchVectorCount { get; private set; }

    public GitHubDataService(GitHubClient gitHub, IDbContextFactory<GitHubDbContext> db, Logger logger)
    {
        _github = gitHub;
        _db = db;
        _logger = logger;
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
        const int TargetMaxApiCallsPerHour = 4_000;
        const int TargetMaxUpdatesPerHour = 3600 * 50;

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
                    foreach ((string repoOwner, string repoName, TimeSpan issueUpdateFrequency, TimeSpan commentUpdateFrequency) in _watchedRepos)
                    {
                        (int apiCalls, int updates) = await UpdateRepositoryDataAsync(repoOwner, repoName, issueUpdateFrequency, commentUpdateFrequency);

                        if (apiCalls > 0 && updates > 0)
                        {
                            TimeSpan sleepTimeCalls = TimeSpan.FromHours(1) / TargetMaxApiCallsPerHour * apiCalls;
                            TimeSpan sleepTimeUpdates = TimeSpan.FromHours(1) / TargetMaxUpdatesPerHour * updates;
                            TimeSpan sleepTime = sleepTimeCalls > sleepTimeUpdates ? sleepTimeCalls : sleepTimeUpdates;
                            _logger.DebugLog($"{nameof(GitHubDataService)}: Performed {apiCalls} API calls (estimate), {updates} DB updates, sleeping for {sleepTime.TotalSeconds:N3} seconds");
                            await Task.Delay(sleepTime, cancellationToken);
                        }
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

    private async Task<(int ApiCalls, int Updates)> UpdateRepositoryDataAsync(string repoOwner, string repoName, TimeSpan issueUpdateFrequency, TimeSpan commentspdateFrequency)
    {
        await using GitHubDbContext dbContext = _db.CreateDbContext();

        HashSet<long> updatedUsers = [];
        HashSet<long> updatedPRsByIssueId = [];
        Dictionary<int, long> issueNumberToId = [];

        var apiOptions = new ApiOptions
        {
            PageSize = 100,
            PageCount = 100,
        };

        int apiCallsPerformed = 0;

        if (!_repositoryIds.TryGetValue((repoOwner, repoName), out long repoId))
        {
            apiCallsPerformed++;
            Repository repo = await _github.Repository.Get(repoOwner, repoName);
            repoId = repo.Id;
            _repositoryIds[(repoOwner, repoName)] = repo.Id;
        }

        RepositoryInfo repoInfo = null;
        repoInfo = await GetOrUpdateRepositoryInfoAsync();
        Debug.Assert(repoId == repoInfo.Id);

        int updatesPerformed = await dbContext.SaveChangesAsync(CancellationToken.None);

        if (repoInfo.Archived)
        {
            return (apiCallsPerformed, updatesPerformed);
        }

        await RefreshStatsAsync();

        DateTime startTime = DateTime.UtcNow;
        if (startTime - repoInfo.LastIssuesUpdate >= issueUpdateFrequency)
        {
            IReadOnlyList<Issue> issues = await _github.Issue.GetAllForRepository(repoId, new RepositoryIssueRequest
            {
                Since = repoInfo.LastIssuesUpdate - DataPollingOffset,
                SortProperty = IssueSort.Updated,
                SortDirection = SortDirection.Descending,
                Filter = IssueFilter.All,
                State = ItemStateFilter.All,
            }, apiOptions);

            Log($"Found {issues.Count} updated issues since {repoInfo.LastIssuesUpdate.ToISODateTime()}", verbose: true);

            apiCallsPerformed += (issues.Count / apiOptions.PageSize.Value) + 1;

            foreach (Issue issue in issues)
            {
                await UpdateIssueInfoAsync(issue);
            }

            repoInfo.LastIssuesUpdate = DeduceLastUpdateTime(issues.Select(i => (i.UpdatedAt ?? i.CreatedAt).UtcDateTime).ToList());
            updatesPerformed += await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        startTime = DateTime.UtcNow;
        if (startTime - repoInfo.LastIssueCommentsUpdate >= commentspdateFrequency)
        {
            IReadOnlyList<IssueComment> issueComments = await _github.Issue.Comment.GetAllForRepository(repoId, new IssueCommentRequest
            {
                Since = repoInfo.LastIssueCommentsUpdate - DataPollingOffset,
                Sort = IssueCommentSort.Updated,
                Direction = SortDirection.Descending,
            }, apiOptions);

            Log($"Found {issueComments.Count} updated issue comments since {repoInfo.LastIssueCommentsUpdate.ToISODateTime()}", verbose: true);

            apiCallsPerformed += (issueComments.Count / apiOptions.PageSize.Value) + 1;

            foreach (IssueComment comment in issueComments)
            {
                await UpdateIssueCommentInfoAsync(comment);

                Log($"Updated issue comment data {comment.HtmlUrl}", verbose: true);
            }

            repoInfo.LastIssueCommentsUpdate = DeduceLastUpdateTime(issueComments.Select(comment => (comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime).ToList());
            updatesPerformed += await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        startTime = DateTime.UtcNow;
        if (startTime - repoInfo.LastPullRequestReviewCommentsUpdate >= commentspdateFrequency)
        {
            IReadOnlyList<PullRequestReviewComment> prReviewComments = await _github.PullRequest.ReviewComment.GetAllForRepository(repoId, new PullRequestReviewCommentRequest
            {
                Since = repoInfo.LastPullRequestReviewCommentsUpdate - DataPollingOffset,
                Sort = PullRequestReviewCommentSort.Updated,
                Direction = SortDirection.Descending,
            }, apiOptions);

            Log($"Found {prReviewComments.Count} updated PR review comments since {repoInfo.LastPullRequestReviewCommentsUpdate.ToISODateTime()}", verbose: true);

            apiCallsPerformed += (prReviewComments.Count / apiOptions.PageSize.Value) + 1;

            foreach (PullRequestReviewComment comment in prReviewComments)
            {
                await UpdatePullRequestReviewCommentInfoAsync(comment);

                Log($"Updated PR review comment data {comment.HtmlUrl}", verbose: true);
            }

            repoInfo.LastPullRequestReviewCommentsUpdate = DeduceLastUpdateTime(prReviewComments.Select(comment => (comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime).ToList());
        }

        updatesPerformed += await dbContext.SaveChangesAsync(CancellationToken.None);
        Log($"Finished updating repository. {apiCallsPerformed} API calls (estimate), {updatesPerformed} DB updates.", verbose: true);
        return (apiCallsPerformed, updatesPerformed);

        void Log(string message, bool verbose = false)
        {
            if (repoInfo is null || verbose)
            {
                return;
            }

            _logger.DebugLog($"{nameof(GitHubDataService)}: {repoInfo.Owner.Login}/{repoInfo.Name} {message}");
        }

        DateTime DeduceLastUpdateTime(List<DateTime> dates)
        {
            if (dates.Count == 0)
            {
                return startTime;
            }

            DateTime min = dates.Min();
            DateTime max = dates.Max();

            Log($"Count={dates.Count} Min={min} Max={max} Start={startTime}", verbose: true);

            if (// Recent update. Next request will fetch everything anyway.
                dates.Count < apiOptions.PageSize.Value / 2 ||
                // This wasn't a recent change.
                startTime - max > TimeSpan.FromDays(1) ||
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
                int recentUpdates = dates.Count(d => startTime - d < recentThreshold);
                if (recentUpdates < dates.Count / 2)
                {
                    max = dates.Where(d => startTime - d > recentThreshold).Max();
                    return true;
                }

                max = default;
                return false;
            }
        }

        static bool ShouldUpdateUserInfo(long currentUserId, User newUser)
        {
            ArgumentNullException.ThrowIfNull(newUser);

            return
                currentUserId == 0 ||
                currentUserId == newUser.Id ||
                newUser.Id != GhostUserId;
        }

        async Task<RepositoryInfo> GetOrUpdateRepositoryInfoAsync()
        {
            RepositoryInfo info = await dbContext.Repositories
                .Where(r => r.Id == repoId)
                .Include(r => r.Owner)
                .Include(r => r.Labels)
                .SingleOrDefaultAsync(CancellationToken.None);

            if (info is not null && DateTime.UtcNow - info.LastRepositoryMetadataUpdate < RepositoryInfoRefreshInterval)
            {
                return info;
            }

            apiCallsPerformed++;
            Repository repo = await _github.Repository.Get(repoId);

            if (info is null)
            {
                info = new RepositoryInfo();
                dbContext.Repositories.Add(info);
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

            apiCallsPerformed++;
            IReadOnlyList<Label> labels = await _github.Issue.Labels.GetAllForRepository(repoId, apiOptions);

            foreach (Label label in labels)
            {
                LabelInfo labelInfo = await dbContext.Labels.FirstOrDefaultAsync(l => l.Id == label.Id, CancellationToken.None);

                if (labelInfo is null)
                {
                    labelInfo = new LabelInfo();
                    dbContext.Labels.Add(labelInfo);
                }

                labelInfo.Id = label.Id;
                labelInfo.NodeIdentifier = label.NodeId;
                labelInfo.Url = label.Url;
                labelInfo.Name = label.Name;
                labelInfo.Color = label.Color;
                labelInfo.Description = label.Description;
                labelInfo.RepositoryId = repoId;
            }

            info.Labels = await dbContext.Labels
                .Where(l => l.RepositoryId == repoId)
                .ToListAsync(CancellationToken.None);

            return info;
        }

        async Task<IssueInfo> UpdateIssueInfoAsync(Issue issue)
        {
            issueNumberToId[issue.Number] = issue.Id;

            IssueInfo info = await dbContext.Issues
                .Where(i => i.Id == issue.Id)
                .Include(i => i.Labels)
                .SingleOrDefaultAsync(CancellationToken.None);

            if (info is not null && info.Body != issue.Body)
            {
                dbContext.BodyEditHistory.Add(new BodyEditHistoryEntry
                {
                    ResourceIdentifier = issue.Id,
                    UpdatedAt = issue.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow,
                    PreviousBody = info.Body,
                    IsComment = false
                });
            }

            if (info is null)
            {
                info = new IssueInfo();
                dbContext.Issues.Add(info);
            }

            PopulateBasicIssueInfo(info, issue);

            info.RepositoryId = repoId;
            info.Labels = [.. repoInfo.Labels.Where(l => issue.Labels.Any(il => il.Id == l.Id))];

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

        async Task UpdatePullRequestInfoAsync(PullRequest pullRequest, IssueInfo issue)
        {
            ArgumentNullException.ThrowIfNull(pullRequest);
            ArgumentNullException.ThrowIfNull(issue);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issue.Id);

            if (!updatedPRsByIssueId.Add(issue.Id))
            {
                return;
            }

            if (pullRequest.Id == 0)
            {
                apiCallsPerformed++;
                pullRequest = await _github.PullRequest.Get(repoId, issue.Number);
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequest.Id);
            ArgumentOutOfRangeException.ThrowIfNotEqual(pullRequest.HtmlUrl, issue.HtmlUrl);

            PullRequestInfo prInfo = await dbContext.PullRequests.SingleOrDefaultAsync(i => i.Id == pullRequest.Id, CancellationToken.None);

            if (prInfo is null)
            {
                prInfo = new PullRequestInfo();
                dbContext.PullRequests.Add(prInfo);
            }

            prInfo.Id = pullRequest.Id;
            prInfo.IssueId = issue.Id;
            prInfo.NodeId = pullRequest.NodeId;
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

        async Task<CommentInfo> GetOrCreateCommentInfoAsync(long commentId, string commentUrl, DateTime updatedAt, string newCommentBody)
        {
            if (await dbContext.Comments.SingleOrDefaultAsync(c => c.Id == commentId, CancellationToken.None) is { } existingComment)
            {
                if (updatedAt < existingComment.UpdatedAt)
                {
                    // Ignore stale update
                    return existingComment;
                }

                if (existingComment.Body != newCommentBody)
                {
                    dbContext.BodyEditHistory.Add(new BodyEditHistoryEntry
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

            if (!issueNumberToId.TryGetValue(issueNumber, out long issueId))
            {
                IssueInfo issueInfo = await dbContext.Issues.SingleOrDefaultAsync(issue => issue.Number == issueNumber && issue.RepositoryId == repoId, CancellationToken.None);

                if (issueInfo is null)
                {
                    apiCallsPerformed++;
                    Issue issue = await _github.Issue.Get(repoId, issueNumber);
                    issueInfo = await UpdateIssueInfoAsync(issue);
                }

                issueId = issueInfo.Id;
            }

            issueNumberToId[issueNumber] = issueId;

            var newComment = new CommentInfo
            {
                Id = commentId,
                IssueId = issueId,
            };

            dbContext.Comments.Add(newComment);

            return newComment;
        }

        async Task UpdateIssueCommentInfoAsync(IssueComment comment)
        {
            CommentInfo info = await GetOrCreateCommentInfoAsync(comment.Id, comment.HtmlUrl, comment.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow, comment.Body);

            info.NodeIdentifier = comment.NodeId;
            info.HtmlUrl = comment.HtmlUrl;
            info.Body = comment.Body;
            info.CreatedAt = comment.CreatedAt.UtcDateTime;
            info.UpdatedAt = (comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime;
            info.AuthorAssociation = comment.AuthorAssociation.Value;
            info.IsPrReviewComment = false;

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
        }

        async Task UpdatePullRequestReviewCommentInfoAsync(PullRequestReviewComment comment)
        {
            DateTime updatedAt = (comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime;

            CommentInfo info = await GetOrCreateCommentInfoAsync(comment.Id, comment.HtmlUrl, updatedAt, comment.Body);

            info.NodeIdentifier = comment.NodeId;
            info.HtmlUrl = comment.HtmlUrl;
            info.Body = comment.Body;
            info.CreatedAt = comment.CreatedAt.UtcDateTime;
            info.UpdatedAt = updatedAt;
            info.AuthorAssociation = comment.AuthorAssociation.Value;
            info.IsPrReviewComment = true;

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
        }

        async Task UpdateUserAsync(User user)
        {
            if (!updatedUsers.Add(user.Id))
            {
                return;
            }

            UserInfo info = await dbContext.Users.SingleOrDefaultAsync(u => u.Id == user.Id, CancellationToken.None);

            if (info is not null && DateTime.UtcNow - info.EntryUpdatedAt < UserInfoRefreshInterval)
            {
                return;
            }

            try
            {
                apiCallsPerformed++;
                user = await _github.User.Get(user.Login);
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
                info = new UserInfo();
                dbContext.Users.Add(info);
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

    public static void PopulateBasicIssueInfo(IssueInfo info, Issue issue)
    {
        info.Id = issue.Id;
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
}
