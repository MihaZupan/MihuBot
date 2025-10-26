using Microsoft.EntityFrameworkCore;
using MihuBot.DB.Models;

#nullable disable

namespace MihuBot.DB.GitHub;

public sealed class GitHubDbContext(DbContextOptions<GitHubDbContext> options) : DbContext(options)
{
    public static class Defaults
    {
        public const string EmbeddingModel = "text-embedding-3-small";
        public const int EmbeddingDimensions = 1536;
    }

    // GitHub data
    public DbSet<IssueInfo> Issues { get; set; }
    public DbSet<RepositoryInfo> Repositories { get; set; }
    public DbSet<PullRequestInfo> PullRequests { get; set; }
    public DbSet<CommentInfo> Comments { get; set; }
    public DbSet<UserInfo> Users { get; set; }
    public DbSet<LabelInfo> Labels { get; set; }
    public DbSet<MilestoneInfo> Milestones { get; set; }
    public DbSet<BodyEditHistoryEntry> BodyEditHistory { get; set; } // TODO - populate

    // Semantic and full text search over issues and comments
    public DbSet<SemanticIngestionBacklogEntry> SemanticIngestionBacklog { get; set; }
    public DbSet<IngestedEmbeddingRecord> IngestedEmbeddings { get; set; }
    public DbSet<TextEntry> TextEntries { get; set; }

    public DbSet<TriagedIssueRecord> TriagedIssues { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Enable pgvector extension
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<TextEntry>(entry =>
        {
            entry
                .HasGeneratedTsVectorColumn(e => e.TextVector, "english", e => e.Text)
                .HasIndex(e => e.TextVector)
                .HasMethod("GIN");
        });

        modelBuilder.Entity<IssueInfo>(issue =>
        {
            issue
                .HasOne(i => i.User)
                .WithMany(u => u.Issues);

            issue
                .HasMany(i => i.Assignees)
                .WithMany(i => i.AssignedIssues);
        });

        base.OnModelCreating(modelBuilder);
    }
}
