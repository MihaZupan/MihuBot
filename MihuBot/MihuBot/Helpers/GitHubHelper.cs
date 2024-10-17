using Octokit;
using System.Text.RegularExpressions;

namespace MihuBot.Helpers;

public static partial class GitHubHelper
{
    public static async IAsyncEnumerable<GitHubComment> PollCommentsAsync(this GitHubClient github, string repoOwner, string repoName, TimeSpan interval, Logger logger)
    {
        List<GitHubComment> commentsToReturn = new();

        int consecutiveFailureCount = 0;
        DateTimeOffset lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
        DateTimeOffset lastCheckTimeIssueComments = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(interval);
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
                    lastCheckTimeReviewComments = pullReviewComments.Max(c => c.UpdatedAt != default ? c.UpdatedAt : c.CreatedAt);
                }

                foreach (PullRequestReviewComment reviewComment in pullReviewComments)
                {
                    commentsToReturn.Add(new GitHubComment(github, repoOwner, repoName, reviewComment.Id, reviewComment.PullRequestUrl, reviewComment.Body, reviewComment.User, IsPrReviewComment: true));
                }

                IReadOnlyList<IssueComment> issueComments = await github.Issue.Comment.GetAllForRepository(repoOwner, repoName, new IssueCommentRequest
                {
                    Since = lastCheckTimeIssueComments,
                    Sort = IssueCommentSort.Created,
                }, new ApiOptions { PageCount = 100 });

                if (issueComments.Count > 0)
                {
                    lastCheckTimeIssueComments = issueComments.Max(c => c.UpdatedAt ?? c.CreatedAt);
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
                    commentsToReturn.Add(new GitHubComment(github, repoOwner, repoName, issueComment.Id, issueComment.HtmlUrl, issueComment.Body, issueComment.User, IsPrReviewComment: false));
                }

                consecutiveFailureCount = 0;
            }
            catch (Exception ex)
            {
                consecutiveFailureCount++;
                lastCheckTimeReviewComments = DateTimeOffset.UtcNow;
                lastCheckTimeIssueComments = DateTimeOffset.UtcNow;
                logger?.DebugLog($"Failed to fetch GitHub notifications: {ex}");

                if (consecutiveFailureCount == 15 * 4) // 15 min
                {
                    await logger?.DebugAsync($"Failed to fetch GitHub notifications: {ex}");
                }

                if (ex is RateLimitExceededException rateLimitEx)
                {
                    TimeSpan toWait = rateLimitEx.GetRetryAfterTimeSpan();
                    if (toWait.TotalSeconds > 1)
                    {
                        logger?.DebugLog($"GitHub polling toWait={toWait}");
                        if (toWait > TimeSpan.FromMinutes(5))
                        {
                            toWait = TimeSpan.FromMinutes(5);
                        }
                        await Task.Delay(toWait);
                    }
                }
            }

            foreach (GitHubComment comment in commentsToReturn)
            {
                yield return comment;
            }

            commentsToReturn.Clear();
        }
    }

    public static bool TryParseGithubRepoAndBranch(string url, out string repository, out string branch)
    {
        Match match = RepoAndBranchRegex().Match(url);
        if (match.Success)
        {
            repository = match.Groups[1].Value;
            branch = match.Groups[2].Value;
            return true;
        }

        repository = null;
        branch = null;
        return false;
    }

    public static bool TryParseDotnetRuntimePRNumber(string input, out int prNumber)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (int.TryParse(parts[0], out prNumber) && prNumber > 0)
        {
            return true;
        }

        return Uri.TryCreate(parts[0], UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/dotnet/runtime/pull/", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(uri.AbsolutePath.Split('/').Last(), out prNumber) &&
            prNumber > 0;
    }

    [GeneratedRegex(@"^https://github\.com/([A-Za-z\d-_]+/[A-Za-z\d-_]+)/(?:tree|blob)/([A-Za-z\d-_]+)(?:[\?#/].*)?$")]
    private static partial Regex RepoAndBranchRegex();

    public static async Task<(bool Valid, bool HasAllScopes)> ValidatePatAsync(HttpClient client, string pat, string[] scopes)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com");
        request.Headers.Add("User-Agent", "MihuBot-PAT-Validation");
        request.Headers.Add("Authorization", $"token {pat}");

        using var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            return (false, false);
        }

        if (response.Headers.TryGetValues("X-OAuth-Scopes", out var scopesHeader))
        {
            var availableScopes = scopesHeader
                .SelectMany(h => h.Split(',', StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return (true, scopes.All(availableScopes.Contains));
        }
        else
        {
            return (true, scopes.Length == 0);
        }
    }
}

public record GitHubComment(GitHubClient Github, string RepoOwner, string RepoName, long CommentId, string Url, string Body, User User, bool IsPrReviewComment)
{
    public int IssueId { get; } = int.Parse(new Uri(Url, UriKind.Absolute).AbsolutePath.Split('/').Last());

    public async Task<Reaction> AddReactionAsync(Octokit.ReactionType reactionType)
    {
        var reaction = new NewReaction(reactionType);

        return await (IsPrReviewComment
            ? Github.Reaction.PullRequestReviewComment.Create(RepoOwner, RepoName, CommentId, reaction)
            : Github.Reaction.IssueComment.Create(RepoOwner, RepoName, CommentId, reaction));
    }
}
