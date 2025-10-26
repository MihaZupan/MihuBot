using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MihuBot.DB.Models;

[Table("ingested_embedding_records")]
[Index(nameof(RepositoryId))]
[Index(nameof(IssueId))]
public sealed class IngestedEmbeddingRecord
{
    public Guid Id { get; set; }
    public long RepositoryId { get; set; }
    public string IssueId { get; set; }
}
