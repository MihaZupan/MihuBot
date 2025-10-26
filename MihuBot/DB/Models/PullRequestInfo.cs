using Octokit;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

[Table("pullrequests")]
public sealed class PullRequestInfo
{
    public string Id { get; set; }
    public DateTime? MergedAt { get; set; }
    public bool IsDraft { get; set; }
    public MergeableState? Mergeable { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int ChangedFiles { get; set; }
    public bool MaintainerCanModify { get; set; }

    public string IssueId { get; set; }
    public IssueInfo Issue { get; set; }

    public long? MergedById { get; set; }
}
