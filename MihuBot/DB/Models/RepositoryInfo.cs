using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

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
    public ICollection<MilestoneInfo> Milestones { get; set; }

    // Data relevant only to data ingestion
    public bool InitialIngestionInProgress { get; set; }
    public string IssueRescanCursor { get; set; } // Null for no cursor, string.Empty when starting new rescan
    public string PullRequestRescanCursor { get; set; } // Null for no cursor, string.Empty when starting new rescan
    public string DiscussionRescanCursor { get; set; } // Null for no cursor, string.Empty when starting new rescan
    public DateTime LastFullRescanTime { get; set; }
    public DateTime LastFullRescanStartTime { get; set; }
    public DateTime LastForceRescanStartTime { get; set; }
    public DateTime LastRepositoryMetadataUpdate { get; set; }
    public DateTime LastIssuesUpdate { get; set; }
    public DateTime LastIssueCommentsUpdate { get; set; }
    public DateTime LastPullRequestReviewCommentsUpdate { get; set; }

    public void UpdateRescanCursors(string value)
    {
        IssueRescanCursor = value;
        PullRequestRescanCursor = value;
        DiscussionRescanCursor = value;
    }
}
