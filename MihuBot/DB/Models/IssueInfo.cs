using Microsoft.EntityFrameworkCore;
using Octokit;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

public enum IssueType
{
    Issue,
    PullRequest,
    Discussion
}

[Table("issues")]
[Index(nameof(Number))]
[Index(nameof(UpdatedAt))]
[Index(nameof(CreatedAt))]
public sealed class IssueInfo
{
    public string Id { get; set; }
    public string HtmlUrl { get; set; }
    public int Number { get; set; }
    public string Title { get; set; }
    public string Body { get; set; }
    public ItemState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public bool Locked { get; set; }
    public LockReason? ActiveLockReason { get; set; }
    public AuthorAssociation AuthorAssociation { get; set; }

    public long UserId { get; set; }
    public UserInfo User { get; set; }

    public long RepositoryId { get; set; }
    public RepositoryInfo Repository { get; set; }

    public string MilestoneId { get; set; }
    public MilestoneInfo Milestone { get; set; }

    public IssueType IssueType { get; set; }
    public PullRequestInfo PullRequest { get; set; }

    public ICollection<CommentInfo> Comments { get; set; }

    public ICollection<LabelInfo> Labels { get; set; }

    public ICollection<UserInfo> Assignees { get; set; }

    public int Plus1 { get; set; }
    public int Minus1 { get; set; }
    public int Laugh { get; set; }
    public int Confused { get; set; }
    public int Heart { get; set; }
    public int Hooray { get; set; }
    public int Eyes { get; set; }
    public int Rocket { get; set; }

    // Data relevant only to data ingestion
    public DateTime LastSemanticIngestionTime { get; set; } = new DateTime(2000, 1, 1, 1, 1, 1, DateTimeKind.Utc);
    public DateTime LastObservedDuringFullRescanTime { get; set; } = new DateTime(2000, 1, 1, 1, 1, 1, DateTimeKind.Utc);
}
