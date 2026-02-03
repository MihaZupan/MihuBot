using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;

#nullable enable

namespace MihuBot.RuntimeUtils.AI;

public sealed record IssueInfoForPrompt(
    string Url,
    string Title,
    string Author,
    string? AuthorAssociation,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    DateTime? PullRequestMergedAt,
    string[] Labels,
    string? Milestone,
    string Body,
    string[] Assignees,
    ReactionsInfoForPrompt? Reactions,
    CommentInfoForPrompt[] Comments,
    string? Miscellaneous)
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static async Task<IssueInfoForPrompt> CreateAsync(IssueInfo issue, IDbContextFactory<GitHubDbContext>? dbContextFactory, CancellationToken cancellationToken, int contextLimitForIssueBody = int.MaxValue, int contextLimitForCommentBody = int.MaxValue)
    {
        CommentInfoForPrompt[] comments = (issue.Comments ?? [])
            .Where(c => !SemanticMarkdownChunker.IsUnlikelyToBeUseful(issue, c, removeSectionsWithoutContext: false, includeBotMessages: true))
            .OrderBy(c => c.CreatedAt)
            .Select(c => new CommentInfoForPrompt(
                c.User.Login,
                c.AuthorAssociation == Octokit.AuthorAssociation.None ? null : c.AuthorAssociation.ToString(),
                c.CreatedAt,
                SemanticMarkdownChunker.TrimTextToTokens(GitHubSemanticSearchIngestionService.Tokenizer, c.Body, contextLimitForCommentBody),
                new ReactionsInfoForPrompt(c.Plus1, c.Minus1, c.Laugh, c.Confused, c.Heart, c.Hooray, c.Eyes, c.Rocket).NullIfEmpty()))
            .ToArray();

        string? miscellaneous = null;

        if (issue.PullRequest is { } pr)
        {
            string draft = pr.IsDraft ? "draft " : "";
            miscellaneous = $"The {draft}pull request changed {pr.ChangedFiles} files with {pr.Additions} additions and {pr.Deletions} deletions.";

            if (dbContextFactory is not null && pr.MergedById is { } userId)
            {
                await using GitHubDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

                if (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken) is UserInfo mergedBy)
                {
                    miscellaneous += $" Merged by {mergedBy.Login}.";
                }
            }
        }

        return new IssueInfoForPrompt(
            issue.HtmlUrl,
            issue.Title,
            issue.User.Login,
            issue.AuthorAssociation == Octokit.AuthorAssociation.None ? null : issue.AuthorAssociation.ToString(),
            issue.CreatedAt,
            issue.ClosedAt,
            issue.PullRequest?.MergedAt,
            issue.Labels is null ? [] : [.. issue.Labels.Select(l => l.Name)],
            issue.Milestone?.Title,
            SemanticMarkdownChunker.TrimTextToTokens(GitHubSemanticSearchIngestionService.Tokenizer, issue.Body, contextLimitForIssueBody),
            issue.Assignees is null ? [] : [.. issue.Assignees.Select(a => a.Login)],
            new ReactionsInfoForPrompt(issue.Plus1, issue.Minus1, issue.Laugh, issue.Confused, issue.Heart, issue.Hooray, issue.Eyes, issue.Rocket).NullIfEmpty(),
            comments,
            miscellaneous);
    }

    public string AsJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }
}

public sealed record CommentInfoForPrompt(
    string Author,
    string? AuthorAssociation,
    DateTime CreatedAt,
    string Body,
    ReactionsInfoForPrompt? Reactions);

public sealed record ReactionsInfoForPrompt(
    int Plus1,
    int Minus1,
    int Laugh,
    int Confused,
    int Heart,
    int Hooray,
    int Eyes,
    int Rocket)
{
    public ReactionsInfoForPrompt? NullIfEmpty() =>
        Plus1 + Minus1 + Laugh + Confused + Heart + Hooray + Eyes + Rocket == 0 ? null : this;
};
