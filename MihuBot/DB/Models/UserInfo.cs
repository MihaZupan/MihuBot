using Microsoft.EntityFrameworkCore;
using Octokit;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

[Table("users")]
[Index(nameof(NodeIdentifier))]
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

    public ICollection<IssueInfo> AssignedIssues { get; set; }

    public DateTime EntryUpdatedAt { get; set; }
}
