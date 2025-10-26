using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

[Table("milestones")]
[Index(nameof(Title))]
public sealed class MilestoneInfo
{
    public string Id { get; set; }
    public string Url { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public int Number { get; set; }
    public int OpenIssueCount { get; set; }
    public int ClosedIssueCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime? DueOn { get; set; }

    public long RepositoryId { get; set; }
    public RepositoryInfo Repository { get; set; }

    public ICollection<IssueInfo> Issues { get; set; }
}
