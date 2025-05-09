using MihuBot.DB.GitHub;
using Octokit;
using System.Text.RegularExpressions;

namespace MihuBot.Helpers;

public static partial class GitHubHelper
{
    public static async Task<BranchReference> TryParseGithubRepoAndBranch(GitHubClient github, string url)
    {
        Match match = RepoAndBranchRegex().Match(url);
        if (!match.Success)
        {
            return null;
        }

        string owner = match.Groups[1].Value;
        string repo = match.Groups[2].Value;
        string branch = match.Groups[3].Value;

        // https://github.com/MihaZupan/MihuBot/blob/master/MihuBot/MihuBot.sln
        // Test if "master/MihuBot" is a branch or directory
        try
        {
            return new BranchReference($"{owner}/{repo}", await github.Repository.Branch.Get(owner, repo, branch));
        }
        catch (NotFoundException) { }

        if (match.Groups[4].Success)
        {
            string pathRemainder = match.Groups[4].Value;
            if (pathRemainder.StartsWith('/'))
            {
                string[] parts = pathRemainder.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < parts.Length && i < 3; i++)
                {
                    branch = $"{branch}/{parts[i]}";

                    try
                    {
                        return new BranchReference($"{owner}/{repo}", await github.Repository.Branch.Get(owner, repo, branch));
                    }
                    catch (NotFoundException) { }
                }
            }
        }

        return null;
    }

    public static bool TryParseIssueOrPRNumber(string input, out int prNumber)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (int.TryParse(parts[0], out prNumber) && prNumber > 0)
        {
            return true;
        }

        return Uri.TryCreate(parts[0], UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath.Contains("/pull/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("/issues/", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(uri.AbsolutePath.Split('/').Last(), out prNumber) &&
            prNumber > 0;
    }

    [GeneratedRegex(@"^https://github\.com/([A-Za-z\d-_]+)/([A-Za-z\d-_]+)/(?:tree|blob)/([A-Za-z\d-_]+)([\?#/].*)?$")]
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

    public static async Task<Reaction> AddReactionAsync(this CommentInfo comment, GitHubClient gitHub, Octokit.ReactionType reactionType)
    {
        var reaction = new NewReaction(reactionType);

        return await (comment.IsPrReviewComment
            ? gitHub.Reaction.PullRequestReviewComment.Create(comment.RepoOwner(), comment.RepoName(), comment.Id, reaction)
            : gitHub.Reaction.IssueComment.Create(comment.RepoOwner(), comment.RepoName(), comment.Id, reaction));
    }

    public static string RepoOwner(this CommentInfo comment) => comment.Issue.Repository.Owner.Login;

    public static string RepoName(this CommentInfo comment) => comment.Issue.Repository.Name;
}

public record BranchReference(string Repository, Branch Branch);
