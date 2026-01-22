using MihuBot.DB.GitHub;

namespace MihuBot.DB;

public static class QueryExtensions
{
    public static IQueryable<IssueInfo> FromDotnetRuntime(this IQueryable<IssueInfo> query)
    {
        return query.Where(i => i.RepositoryId == 210716005);
    }

    public static IQueryable<RepositoryInfo> OnlyDotnetRuntime(this IQueryable<RepositoryInfo> query)
    {
        return query.Where(r => r.Id == 210716005);
    }

    public static string ToDisplayString(this IssueType issueType)
    {
        return issueType switch
        {
            IssueType.Issue => "Issue",
            IssueType.PullRequest => "Pull Request",
            IssueType.Discussion => "Discussion",
            _ => throw new UnreachableException(),
        };
    }
}
