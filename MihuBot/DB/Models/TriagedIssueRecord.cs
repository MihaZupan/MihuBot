using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;

#nullable disable

namespace MihuBot.DB.Models;

[Table("triaged_issues")]
[Index(nameof(IssueId))]
public sealed class TriagedIssueRecord
{
    public long Id { get; set; } // Auto
    public DateTime UpdatedAt { get; set; }
    public string Body { get; set; }
    public int TriageReportIssueNumber { get; set; }

    public string IssueId { get; set; }
    public IssueInfo Issue { get; set; }
}
