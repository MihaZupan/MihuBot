using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace MihuBot.DB.GitHubFts;

public sealed class GitHubFtsDbContext : DbContext
{
    public GitHubFtsDbContext(DbContextOptions<GitHubFtsDbContext> options) : base(options) { }

    public DbSet<TextEntry> TextEntries { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TextEntryConfiguration());

        base.OnModelCreating(modelBuilder);
    }

    private sealed class TextEntryConfiguration : IEntityTypeConfiguration<TextEntry>
    {
        public void Configure(EntityTypeBuilder<TextEntry> builder)
        {
            builder
                .HasGeneratedTsVectorColumn(e => e.TextVector, "english", e => e.Text)
                .HasIndex(e => e.TextVector)
                .HasMethod("GIN");
        }
    }
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
    public NpgsqlTsVector TextVector { get; set; }
}
