using MihuBot.DB.GitHub;

namespace MihuBot.DB;

public static class QueryExtensions
{
    public static IQueryable<IssueInfo> FromDotnetRuntime(this IQueryable<IssueInfo> query)
    {
        return query.Where(i => i.Repository.FullName == "dotnet/runtime");
    }
}
