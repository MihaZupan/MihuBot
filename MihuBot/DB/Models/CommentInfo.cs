using Microsoft.EntityFrameworkCore;
using Octokit;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

[Table("comments")]
[Index(nameof(UpdatedAt))]
public sealed class CommentInfo
{
    public string Id { get; set; }
    public string HtmlUrl { get; set; }
    public string Body { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public AuthorAssociation AuthorAssociation { get; set; }
    public bool IsMinimized { get; set; }
    public string MinimizedReason { get; set; }

    public bool IsPrReviewComment { get; set; }
    public long GitHubIdentifier { get; set; }

    public string IssueId { get; set; }
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

    // Data relevant only to data ingestion
    public DateTime LastObservedDuringFullRescanTime { get; set; } = new DateTime(2000, 1, 1, 1, 1, 1, DateTimeKind.Utc);
}
