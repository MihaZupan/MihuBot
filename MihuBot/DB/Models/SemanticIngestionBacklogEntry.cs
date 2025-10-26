using System.ComponentModel.DataAnnotations.Schema;

namespace MihuBot.DB.GitHub;

#nullable disable

[Table("semantic_ingestion_backlog")]
public sealed class SemanticIngestionBacklogEntry
{
    public long Id { get; set; }
    public string IssueId { get; set; }
}
