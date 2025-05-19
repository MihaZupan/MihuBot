using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace MihuBot.DB.GitHub;

public sealed class GitHubDbContext : DbContext
{
    public GitHubDbContext(DbContextOptions<GitHubDbContext> options) : base(options) { }

    public DbSet<IssueInfo> Issues { get; set; }
    public DbSet<RepositoryInfo> Repositories { get; set; }
    public DbSet<PullRequestInfo> PullRequests { get; set; }
    public DbSet<CommentInfo> Comments { get; set; }
    public DbSet<UserInfo> Users { get; set; }
    public DbSet<LabelInfo> Labels { get; set; }
    public DbSet<BodyEditHistoryEntry> BodyEditHistory { get; set; }
    public DbSet<IngestedEmbeddingRecord> IngestedEmbeddings { get; set; }
    public DbSet<TriagedIssueRecord> TriagedIssues { get; set; }
}

[Table("issues")]
[Index(nameof(Number))]
[Index(nameof(UpdatedAt))]
[Index(nameof(CreatedAt))]
public sealed class IssueInfo
{
    public long Id { get; set; }
    public string NodeIdentifier { get; set; }
    public string HtmlUrl { get; set; }
    public int Number { get; set; }
    public string Title { get; set; }
    public string Body { get; set; }
    public ItemState State { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool Locked { get; set; }
    public LockReason? ActiveLockReason { get; set; }

    public long UserId { get; set; }
    public UserInfo User { get; set; }

    public long RepositoryId { get; set; }
    public RepositoryInfo Repository { get; set; }

    public PullRequestInfo PullRequest { get; set; }

    public ICollection<CommentInfo> Comments { get; set; }

    public ICollection<LabelInfo> Labels { get; set; }

    public int Plus1 { get; set; }
    public int Minus1 { get; set; }
    public int Laugh { get; set; }
    public int Confused { get; set; }
    public int Heart { get; set; }
    public int Hooray { get; set; }
    public int Eyes { get; set; }
    public int Rocket { get; set; }
}

[Table("repositories")]
public sealed class RepositoryInfo
{
    public long Id { get; set; }
    public string NodeIdentifier { get; set; }
    public string HtmlUrl { get; set; }
    public string Name { get; set; }
    public string FullName { get; set; }
    public string Description { get; set; }
    public bool Private { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public long OwnerId { get; set; }
    public UserInfo Owner { get; set; }

    public ICollection<IssueInfo> Issues { get; set; }
    public ICollection<LabelInfo> Labels { get; set; }

    public DateTime LastRepositoryMetadataUpdate { get; set; }
    public DateTime LastIssuesUpdate{ get; set; }
    public DateTime LastIssueCommentsUpdate { get; set; }
    public DateTime LastPullRequestReviewCommentsUpdate { get; set; }
}

[Table("pullrequests")]
public sealed class PullRequestInfo
{
    public long Id { get; set; }
    public string NodeId { get; set; }
    public DateTime? MergedAt { get; set; }
    public bool Draft { get; set; }
    public bool? Mergeable { get; set; }
    public MergeableState? MergeableState { get; set; }
    public string MergeCommitSha { get; set; }
    public int Commits { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int ChangedFiles { get; set; }
    public bool? MaintainerCanModify { get; set; }

    public long IssueId { get; set; }
    public IssueInfo Issue { get; set; }

    public long? MergedById { get; set; }
}

[Table("comments")]
[Index(nameof(UpdatedAt))]
public sealed class CommentInfo
{
    public long Id { get; set; }
    public string NodeIdentifier { get; set; }
    public string HtmlUrl { get; set; }
    public string Body { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public AuthorAssociation AuthorAssociation { get; set; }
    public bool IsPrReviewComment { get; set; }

    public long IssueId { get; set; }
    public IssueInfo Issue { get; set; }

    public long UserId { get; set; }
    public UserInfo User { get; set; }

    public int Plus1 { get; set; }
    public int Minus1 { get; set; }
    public int Laugh { get; set; }
    public int Confused { get; set; }
    public int Heart { get; set; }
    public int Hooray { get; set; }
    public int Eyes { get; set; }
    public int Rocket { get; set; }
}

[Table("users")]
public sealed class UserInfo
{
    public long Id { get; set; }
    public string NodeIdentifier { get; set; }
    public string Login { get; set; }
    public string Name { get; set; }
    public string HtmlUrl { get; set; }
    public int Followers { get; set; }
    public int Following { get; set; }
    public string Company { get; set; }
    public string Location { get; set; }
    public string Bio { get; set; }
    public AccountType? Type { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<IssueInfo> Issues { get; set; }

    public DateTime EntryUpdatedAt { get; set; }
}

[Table("labels")]
public sealed class LabelInfo
{
    public long Id { get; set; }
    public string NodeIdentifier { get; set; }
    public string Url { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
    public string Description { get; set; }

    public long RepositoryId { get; set; }
    public RepositoryInfo Repository { get; set; }

    public ICollection<IssueInfo> Issues { get; set; }
}

[Table("body_edit_history")]
public sealed class BodyEditHistoryEntry
{
    public long Id { get; set; }
    public long ResourceIdentifier { get; set; }
    public bool IsComment { get; set; }
    public string PreviousBody { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Table("ingested_embedding_history")]
[Index(nameof(ResourceIdentifier))]
public sealed class IngestedEmbeddingRecord
{
    public Guid Id { get; set; }
    public long ResourceIdentifier { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Table("triaged_issues")]
[Index(nameof(IssueId))]
public sealed class TriagedIssueRecord
{
    public long Id { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Body { get; set; }
    public int TriageReportIssueNumber { get; set; }

    public long IssueId { get; set; }
    public IssueInfo Issue { get; set; }
}
