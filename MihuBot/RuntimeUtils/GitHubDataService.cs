using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils;

public sealed class GitHubDataService : IHostedService
{
    private static readonly TimeSpan UserInfoRefreshInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan RepositoryInfoRefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DataPollingOffset = TimeSpan.FromSeconds(2);

    private readonly (string Owner, string Name, TimeSpan IssueUpdateFrequency, TimeSpan CommentUpdateFrequency)[] _watchedRepos =
    [
        ("dotnet", "runtime", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15))
        // ("dotnet", "yarp", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60))
    ];

    private readonly GitHubClient _github;
    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly Dictionary<(string Owner, string Name), long> _repositoryIds = [];
    private Task _updatesTask;

    public GitHubDataService(GitHubClient gitHub, IDbContextFactory<GitHubDbContext> db, Logger logger)
    {
        _github = gitHub;
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        _updatesTask = Task.Run(async () =>
        {
            const int TargetMaxUpdatesPerHour = 20_000;

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
                            int updates = await UpdateRepositoryDataAsync(repoOwner, repoName, issueUpdateFrequency, commentUpdateFrequency, cancellationToken);

                            if (updates > 0)
                            {
                                TimeSpan sleepTime = TimeSpan.FromHours(1) / TargetMaxUpdatesPerHour * updates;
                                _logger.DebugLog($"Performed {updates} updates, sleeping for {sleepTime.TotalSeconds:N3} seconds");
                                await Task.Delay(sleepTime, cancellationToken);
                            }
                        }

                        consecutiveFailureCount = 0;
                    }
                    catch (Exception ex)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            consecutiveFailureCount++;

                            string errorMessage = $"{nameof(GitHubDataService)}: Update failed: {ex}";
                            _logger.DebugLog(errorMessage);

                            if (consecutiveFailureCount == 15 * 4) // 15 min
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
                                    await Task.Delay(toWait);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected exception: {ex}");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _updateCts.CancelAsync();

        if (_updatesTask is not null)
        {
            await _updatesTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task<int> UpdateRepositoryDataAsync(string repoOwner, string repoName, TimeSpan issueUpdateFrequency, TimeSpan commentspdateFrequency, CancellationToken cancellationToken)
    {
        await using GitHubDbContext dbContext = _db.CreateDbContext();

        Dictionary<long, UserInfo> updatedUsers = [];
        Dictionary<int, long> issueNumberToId = [];

        if (!_repositoryIds.TryGetValue((repoOwner, repoName), out long repoId))
        {
            Repository repo = await _github.Repository.Get(repoOwner, repoName);
            repoId = repo.Id;
            _repositoryIds[(repoOwner, repoName)] = repo.Id;
        }

        RepositoryInfo repoInfo = await GetOrUpdateRepositoryInfoAsync();

        if (repoInfo.Archived)
        {
            return await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        var apiOptions = new ApiOptions
        {
            PageSize = 100,
            PageCount = 2
        };

        DateTime startTime = DateTime.UtcNow;
        if (startTime - repoInfo.LastIssuesUpdate >= issueUpdateFrequency)
        {
            Log("Fetching updated issues");
            IReadOnlyList<Issue> issues = await _github.Issue.GetAllForRepository(repoId, new RepositoryIssueRequest
            {
                Since = repoInfo.LastIssuesUpdate - DataPollingOffset,
                SortProperty = IssueSort.Updated,
                SortDirection = SortDirection.Ascending
            }, apiOptions);
            Log($"Found {issues.Count} updated issues");

            foreach (Issue issue in issues)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await UpdateIssueInfoAsync(issue);

                Log($"Updated issue metadata {issue.Number}: {issue.Title}");
            }

            DateTime updateTime = issues.Count > 0 ? issues.Max(issue => issue.UpdatedAt ?? issue.CreatedAt).UtcDateTime : startTime;
            (await dbContext.Repositories.SingleAsync(r => r.Id == repoId, CancellationToken.None)).LastIssuesUpdate = updateTime;
        }

        startTime = DateTime.UtcNow;
        if (startTime - repoInfo.LastIssueCommentsUpdate >= commentspdateFrequency)
        {
            Log("Fetching updated issue comments");
            IReadOnlyList<IssueComment> issueComments = await _github.Issue.Comment.GetAllForRepository(repoId, new IssueCommentRequest
            {
                Since = repoInfo.LastIssueCommentsUpdate - DataPollingOffset,
                Sort = IssueCommentSort.Updated,
                Direction = SortDirection.Ascending,
            }, apiOptions);
            Log($"Found {issueComments.Count} updated issue comments");

            foreach (IssueComment comment in issueComments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await UpdateIssueCommentInfoAsync(comment);

                Log($"Updated issue comment data {comment.HtmlUrl}");
            }

            DateTime updateTime = issueComments.Count > 0 ? issueComments.Max(comment => comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime : startTime;
            (await dbContext.Repositories.SingleAsync(r => r.Id == repoId, CancellationToken.None)).LastIssueCommentsUpdate = updateTime;
        }

        startTime = DateTime.UtcNow;
        if (startTime - repoInfo.LastPullRequestReviewCommentsUpdate >= commentspdateFrequency)
        {
            Log("Fetching updated PR review comments");
            IReadOnlyList<PullRequestReviewComment> prReviewComments = await _github.PullRequest.ReviewComment.GetAllForRepository(repoId, new PullRequestReviewCommentRequest
            {
                Since = repoInfo.LastPullRequestReviewCommentsUpdate - DataPollingOffset,
                Sort = PullRequestReviewCommentSort.Updated,
                Direction = SortDirection.Ascending,
            }, apiOptions);
            Log($"Found {prReviewComments.Count} updated PR review comments");

            foreach (PullRequestReviewComment comment in prReviewComments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await UpdatePullRequestReviewCommentInfoAsync(comment);

                Log($"Updated PR review comment data {comment.HtmlUrl}");
            }

            DateTime updateTime = prReviewComments.Count > 0 ? prReviewComments.Max(comment => comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime : startTime;
            (await dbContext.Repositories.SingleAsync(r => r.Id == repoId, CancellationToken.None)).LastPullRequestReviewCommentsUpdate = updateTime;
        }

        Log("Finished updating repository");
        return await dbContext.SaveChangesAsync(CancellationToken.None);

        void Log(string message)
        {
            _logger.DebugLog($"{nameof(GitHubDataService)}: {repoInfo.Owner.Login}/{repoInfo.Name} {message}");
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

            Repository repo = await _github.Repository.Get(repoId);

            info = new RepositoryInfo
            {
                Id = repoId,
                NodeIdentifier = repo.NodeId,
                HtmlUrl = repo.HtmlUrl,
                Name = repo.Name,
                FullName = repo.FullName,
                Description = repo.Description,
                OwnerId = repo.Owner.Id,
                CreatedAt = repo.CreatedAt.UtcDateTime,
                UpdatedAt = repo.UpdatedAt.UtcDateTime,
                Private = repo.Private,
                Archived = repo.Archived,
                LastRepositoryMetadataUpdate = DateTime.UtcNow,
                LastIssuesUpdate = info?.LastIssuesUpdate ?? repo.CreatedAt.UtcDateTime,
                LastIssueCommentsUpdate = info?.LastIssueCommentsUpdate ?? repo.CreatedAt.UtcDateTime,
                LastPullRequestReviewCommentsUpdate = info?.LastPullRequestReviewCommentsUpdate ?? repo.CreatedAt.UtcDateTime,
            };

            dbContext.Repositories.Add(info);

            info.Owner = await UpdateUserAsync(repo.Owner);

            IReadOnlyList<Label> labels = await _github.Issue.Labels.GetAllForRepository(repoId);

            foreach (Label label in labels)
            {
                if (await dbContext.Labels.FirstOrDefaultAsync(l => l.Id == label.Id, CancellationToken.None) is { } existingLabel)
                {
                    dbContext.Labels.Remove(existingLabel);
                }

                dbContext.Labels.Add(new LabelInfo
                {
                    Id = label.Id,
                    NodeIdentifier = label.NodeId,
                    Url = label.Url,
                    Name = label.Name,
                    Color = label.Color,
                    Description = label.Description,
                    RepositoryId = repoId,
                });
            }

            info.Labels = await dbContext.Labels
                .Where(l => l.RepositoryId == repoId)
                .ToListAsync(CancellationToken.None);

            return info;
        }

        async Task<IssueInfo> UpdateIssueInfoAsync(Issue issue)
        {
            issueNumberToId[issue.Number] = issue.Id;

            if (await dbContext.Issues
                .Where(i => i.Id == issue.Id)
                .Include(i => i.Labels)
                .SingleOrDefaultAsync(CancellationToken.None) is { } existingIssue)
            {
                if (existingIssue.Body != issue.Body)
                {
                    dbContext.BodyEditHistory.Add(new BodyEditHistoryEntry
                    {
                        ResourceIdentifier = issue.Id,
                        UpdatedAt = issue.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow,
                        PreviousBody = existingIssue.Body,
                        IsComment = false
                    });
                }

                dbContext.Issues.Remove(existingIssue);
            }

            var info = new IssueInfo
            {
                Id = issue.Id,
                NodeIdentifier = issue.NodeId,
                HtmlUrl = issue.HtmlUrl,
                Number = issue.Number,
                Title = issue.Title,
                Body = issue.Body,
                State = issue.State.Value,
                CreatedAt = issue.CreatedAt.UtcDateTime,
                UpdatedAt = (issue.UpdatedAt ?? issue.CreatedAt).UtcDateTime,
                ClosedAt = issue.ClosedAt?.UtcDateTime,
                Locked = issue.Locked,
                ActiveLockReason = issue.ActiveLockReason?.Value,
                UserId = issue.User.Id,
                RepositoryId = repoId,

                Plus1 = issue.Reactions.Plus1,
                Minus1 = issue.Reactions.Minus1,
                Laugh = issue.Reactions.Laugh,
                Confused = issue.Reactions.Confused,
                Eyes = issue.Reactions.Eyes,
                Heart = issue.Reactions.Heart,
                Hooray = issue.Reactions.Hooray,
                Rocket = issue.Reactions.Rocket,
            };

            //List<long> labelIds = [.. issue.Labels.Select(l => l.Id)];
            //var existingLabels = dbContext.Issues.Find(issue.Id).Labels;

            //info.Labels = await dbContext.Labels
            //    .Where(l => labelIds.Contains(l.Id))
            //    .ToListAsync(CancellationToken.None);

            info.Labels = [.. repoInfo.Labels.Where(l => issue.Labels.Any(il => il.Id == l.Id))];

            dbContext.Issues.Add(info);

            await UpdateUserAsync(issue.User);

            if (issue.PullRequest is { } pullRequest)
            {
                if (await dbContext.PullRequests.SingleOrDefaultAsync(i => i.Id == issue.Id, CancellationToken.None) is { } existingPullRequest)
                {
                    dbContext.PullRequests.Remove(existingPullRequest);
                }

                dbContext.PullRequests.Add(new PullRequestInfo
                {
                    Id = pullRequest.Id,
                    IssueId = issue.Id,
                });
            }

            return info;
        }

        async Task<long> RemoveExistingCommentAndGetIssueIdAsync(long commentId, string commentUrl, DateTime updatedAt, string newCommentBody)
        {
            long issueId = -1;

            if (await dbContext.Comments.SingleOrDefaultAsync(c => c.Id == commentId, CancellationToken.None) is { } existingComment)
            {
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

                issueId = existingComment.IssueId;
                dbContext.Comments.Remove(existingComment);
            }

            if (!GitHubHelper.TryParseIssueOrPRNumber(commentUrl, out int issueNumber))
            {
                throw new Exception($"Failed to parse issue number from comment URL: {commentUrl}");
            }

            if (issueId == -1 && !issueNumberToId.TryGetValue(issueNumber, out issueId))
            {
                IssueInfo issueInfo = await dbContext.Issues.SingleOrDefaultAsync(issue => issue.Number == issueNumber && issue.RepositoryId == repoId, CancellationToken.None);

                if (issueInfo is null)
                {
                    Issue issue = await _github.Issue.Get(repoId, issueNumber);
                    issueInfo = await UpdateIssueInfoAsync(issue);
                }

                issueId = issueInfo.Id;
            }

            issueNumberToId[issueNumber] = issueId;

            return issueId;
        }

        async Task UpdateIssueCommentInfoAsync(IssueComment comment)
        {
            long issueId = await RemoveExistingCommentAndGetIssueIdAsync(comment.Id, comment.HtmlUrl, comment.UpdatedAt?.UtcDateTime ?? DateTime.UtcNow, comment.Body);

            dbContext.Comments.Add(new CommentInfo
            {
                Id = comment.Id,
                NodeIdentifier = comment.NodeId,
                HtmlUrl = comment.HtmlUrl,
                Body = comment.Body,
                CreatedAt = comment.CreatedAt.UtcDateTime,
                UpdatedAt = (comment.UpdatedAt ?? comment.CreatedAt).UtcDateTime,
                AuthorAssociation = comment.AuthorAssociation.Value,
                IssueId = issueId,
                UserId = comment.User.Id,
                IsPrReviewComment = false,

                Plus1 = comment.Reactions.Plus1,
                Minus1 = comment.Reactions.Minus1,
                Laugh = comment.Reactions.Laugh,
                Confused = comment.Reactions.Confused,
                Eyes = comment.Reactions.Eyes,
                Heart = comment.Reactions.Heart,
                Hooray = comment.Reactions.Hooray,
                Rocket = comment.Reactions.Rocket,
            });

            await UpdateUserAsync(comment.User);
        }

        async Task UpdatePullRequestReviewCommentInfoAsync(PullRequestReviewComment comment)
        {
            DateTime updatedAt = (comment.UpdatedAt.Year > 2000 ? comment.UpdatedAt : comment.CreatedAt).UtcDateTime;

            long issueId = await RemoveExistingCommentAndGetIssueIdAsync(comment.Id, comment.HtmlUrl, updatedAt, comment.Body);

            dbContext.Comments.Add(new CommentInfo
            {
                Id = comment.Id,
                NodeIdentifier = comment.NodeId,
                HtmlUrl = comment.HtmlUrl,
                Body = comment.Body,
                CreatedAt = comment.CreatedAt.UtcDateTime,
                UpdatedAt = updatedAt,
                AuthorAssociation = comment.AuthorAssociation.Value,
                IssueId = issueId,
                UserId = comment.User.Id,
                IsPrReviewComment = true,

                Plus1 = comment.Reactions.Plus1,
                Minus1 = comment.Reactions.Minus1,
                Laugh = comment.Reactions.Laugh,
                Confused = comment.Reactions.Confused,
                Eyes = comment.Reactions.Eyes,
                Heart = comment.Reactions.Heart,
                Hooray = comment.Reactions.Hooray,
                Rocket = comment.Reactions.Rocket,
            });

            await UpdateUserAsync(comment.User);
        }

        async Task<UserInfo> UpdateUserAsync(User user)
        {
            if (updatedUsers.TryGetValue(user.Id, out UserInfo info))
            {
                return info;
            }

            if (await dbContext.Users.SingleOrDefaultAsync(u => u.Id == user.Id, CancellationToken.None) is { } existingUser)
            {
                if (DateTime.UtcNow - existingUser.EntryUpdatedAt < UserInfoRefreshInterval)
                {
                    updatedUsers.Add(user.Id, existingUser);
                    return existingUser;
                }

                dbContext.Users.Remove(existingUser);
            }

            user = await _github.User.Get(user.Login);

            info = new UserInfo
            {
                Id = user.Id,
                NodeIdentifier = user.NodeId,
                Login = user.Login,
                Name = user.Name,
                HtmlUrl = user.HtmlUrl,
                Followers = user.Followers,
                Following = user.Following,
                Company = user.Company,
                Location = user.Location,
                Bio = user.Bio,
                Type = user.Type,
                CreatedAt = user.CreatedAt.UtcDateTime,
                EntryUpdatedAt = DateTime.UtcNow,
            };

            dbContext.Users.Add(info);

            updatedUsers.Add(user.Id, info);
            return info;
        }
    }
}
