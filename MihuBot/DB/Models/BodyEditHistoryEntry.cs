using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.Models;

[Table("body_edit_history")]
public sealed class BodyEditHistoryEntry
{
    public long Id { get; set; } // Auto
    public string ResourceIdentifier { get; set; }
    public bool IsComment { get; set; }
    public string PreviousBody { get; set; }
    public DateTime UpdatedAt { get; set; }
}
