using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MihuBot.DB.GitHubFts;

public sealed class GitHubFtsDbContext : DbContext
{
    public GitHubFtsDbContext(DbContextOptions<GitHubFtsDbContext> options) : base(options) { }

    public DbSet<TextEntry> TextEntries { get; set; }
}

[Table("text_entries")]
[Index(nameof(RepositoryId))]
[Index(nameof(IssueId))]
public sealed class TextEntry
{
    public Guid Id { get; set; }
    public long RepositoryId { get; set; }
    public string IssueId { get; set; }
    public string SubIdentifier { get; set; }
    public string Text { get; set; }
    public DateTime UpdatedAt { get; set; }
}
