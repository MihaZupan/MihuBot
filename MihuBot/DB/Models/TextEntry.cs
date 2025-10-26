using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

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
