using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using MihuBot.DB.GitHub;
using Octokit;

#nullable enable

namespace MihuBot.Helpers;

public static partial class GitHubHelper
{
    private const int CopilotUserId = 198982749;

    public static async Task<BranchReference?> TryParseGithubRepoAndBranch(GitHubClient github, string url)
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
            ? gitHub.Reaction.PullRequestReviewComment.Create(comment.RepoOwner(), comment.RepoName(), comment.GitHubIdentifier, reaction)
            : gitHub.Reaction.IssueComment.Create(comment.RepoOwner(), comment.RepoName(), comment.GitHubIdentifier, reaction));
    }

    public static string RepoOwner(this IssueInfo issue) => issue.Repository.Owner.Login;

    public static string RepoName(this IssueInfo issue) => issue.Repository.Name;

    public static string RepoOwner(this CommentInfo comment) => comment.Issue.RepoOwner();

    public static string RepoName(this CommentInfo comment) => comment.Issue.RepoName();

    public static bool TryParseIssueOrPRNumber(string input, out int prNumber) =>
        TryParseIssueOrPRNumber(input, out _, out prNumber);

    public static bool TryParseIssueOrPRNumber(string? input, [NotNullWhen(true)] out string? org, [NotNullWhen(true)] out string? repoName, out int prNumber)
    {
        if (!TryParseIssueOrPRNumber(input, out string? fullRepoName, out prNumber) || fullRepoName is null)
        {
            org = null;
            repoName = null;
            return false;
        }

        string[] parts = fullRepoName.Split('/');
        (org, repoName) = (parts[0], parts[1]);
        return true;
    }

    public static bool TryParseIssueOrPRNumber(string? input, out string? repoName, out int prNumber)
    {
        if (TryParseRepoOwnerAndName(input, out string? repoOwner, out string? repo, out string[]? extra) &&
            extra.Length == 2 &&
            (extra[0].Equals("pull/", StringComparison.OrdinalIgnoreCase) || extra[0].Equals("issues/", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(extra[1], out prNumber) &&
            prNumber > 0)
        {
            repoName = $"{repoOwner}/{repo}";
            return true;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            repoName = null;
            prNumber = 0;
            return false;
        }

        input = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Trim('#', '<', '>');

        if (int.TryParse(input, out prNumber) && prNumber > 0)
        {
            repoName = null;
            return true;
        }

        repoName = null;
        return false;
    }

    public static bool TryParseRepoOwnerAndName(string? input, [NotNullWhen(true)] out string? repoOwner, [NotNullWhen(true)] out string? repoName, [NotNullWhen(true)] out string[]? extra)
    {
        repoOwner = null;
        repoName = null;
        extra = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        input = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Trim('#', '<', '>');

        // https://github.com/dotnet/runtime/issues/111492
        // "/", "dotnet/", "runtime/", "issues/", "111492"
        if (Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.Segments is { Length: > 2 } segments)
        {
            repoOwner = segments[1].TrimEnd('/');
            repoName = segments[2].TrimEnd('/');
            extra = segments.AsSpan(3).ToArray();
            return true;
        }

        return false;
    }

    private static readonly SearchValues<string> s_botNameChunks = SearchValues.Create(
        ["[bot]", "-service", "-agent", "copilot", "-pipeline", "-action", "-aspnet"],
        StringComparison.OrdinalIgnoreCase);

    public static bool IsLikelyARealUser(this UserInfo user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Type != AccountType.User)
        {
            return false;
        }

        if (user.Id == CopilotUserId)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(user.Login) ||
            user.Login.ContainsAny(s_botNameChunks) ||
            user.Login.EndsWith("Bot", StringComparison.Ordinal) ||
            user.Login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(user.Bio) &&
            user.Bio.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static async Task<IReadOnlyList<Issue>> GetAllSubIssuesAsync(this GitHubClient client, long repositoryId, long issueNumber, ApiOptions? options = null)
    {
        var connection = new ApiConnection(client.Connection);

        return await connection.GetAll<Issue>(new Uri($"repositories/{repositoryId}/issues/{issueNumber}/sub_issues", UriKind.Relative), options ?? ApiOptions.None);
    }
}

public record BranchReference(string Repository, Branch Branch);
