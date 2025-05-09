using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;

namespace MihuBot.RuntimeUtils;

public sealed class GitHubSearchService
{
    public const string EmbeddingModel = "text-embedding-3-large";

    private readonly IDbContextFactory<GitHubDbContext> _db;

    public GitHubSearchService(IDbContextFactory<GitHubDbContext> db) => _db = db;

    public record IssueSearchResult(double Score, IssueInfo Issue);

    public async Task<IssueSearchResult[]> SearchIssuesAsync(string query, CancellationToken cancellationToken)
    {
        await using GitHubDbContext db = await _db.CreateDbContextAsync(cancellationToken);

        List<IssueInfo> result = await db.Issues
            .AsNoTracking()
            .Where(i => i.Body.Contains(query))
            .Where(i => i.Repository.Name == "runtime" && i.Repository.Owner.Login == "dotnet")
            .Include(i => i.Labels)
            .Include(i => i.User)
            .Include(i => i.Repository)
            .Take(100)
            .ToListAsync(cancellationToken);

        Console.WriteLine($"Search for '{query}' returned {result.Count} results");

        var comments = await db.Comments
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return result
            .Select(item => new IssueSearchResult(new Random(item.Number).NextDouble(), item))
            .ToArray();
    }
}
