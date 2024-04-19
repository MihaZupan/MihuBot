using Octokit;

namespace MihuBot.Helpers;

public static class GitHubHelper
{
    public static async IAsyncEnumerable<GitHubComment> PollCommentsAsync(this GitHubClient github, string repoOwner, string repoName, Logger logger = null)
    {
        List<GitHubComment> commentsToReturn = new();

        DateTimeOffset lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
        DateTimeOffset lastCheckTimeIssueComments = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                IReadOnlyList<PullRequestReviewComment> pullReviewComments = await github.PullRequest.ReviewComment.GetAllForRepository(repoOwner, repoName, new PullRequestReviewCommentRequest
                {
                    Since = lastCheckTimeReviewComments
                }, new ApiOptions { PageCount = 100 });

                if (pullReviewComments.Count > 0)
                {
                    lastCheckTimeReviewComments = pullReviewComments.Max(c => c.CreatedAt);
                }

                foreach (PullRequestReviewComment reviewComment in pullReviewComments)
                {
                    commentsToReturn.Add(new GitHubComment(repoOwner, repoName, reviewComment.Id, reviewComment.PullRequestUrl, reviewComment.Body, reviewComment.User));
                }

                IReadOnlyList<IssueComment> issueComments = await github.Issue.Comment.GetAllForRepository(repoOwner, repoName, new IssueCommentRequest
                {
                    Since = lastCheckTimeIssueComments,
                    Sort = IssueCommentSort.Created,
                }, new ApiOptions { PageCount = 100 });

                if (issueComments.Count > 0)
                {
                    lastCheckTimeIssueComments = issueComments.Max(c => c.CreatedAt);
                }

                var recentTimestamp = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);

                if (recentTimestamp > lastCheckTimeIssueComments)
                {
                    lastCheckTimeIssueComments = recentTimestamp;
                }

                if (recentTimestamp > lastCheckTimeReviewComments)
                {
                    lastCheckTimeReviewComments = recentTimestamp;
                }

                foreach (IssueComment issueComment in issueComments)
                {
                    commentsToReturn.Add(new GitHubComment(repoOwner, repoName, issueComment.Id, issueComment.HtmlUrl, issueComment.Body, issueComment.User));
                }
            }
            catch (Exception ex)
            {
                lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
                lastCheckTimeIssueComments = DateTime.UtcNow;
                logger?.DebugLog($"Failed to fetch GitHub notifications: {ex}");
            }

            foreach (GitHubComment comment in commentsToReturn)
            {
                yield return comment;
            }

            commentsToReturn.Clear();
        }
    }
}

public record GitHubComment(string RepoOwner, string RepoName, int CommentId, string Url, string Body, User User)
{
    public int IssueId { get; } = int.Parse(new Uri(Url, UriKind.Absolute).AbsolutePath.Split('/').Last());
}
