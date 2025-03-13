using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

#nullable disable

namespace StorageService.Storage;

[Table("files")]
[Index(nameof(ExpiresAt))] // For cleanup queries
public sealed class FileDbEntry
{
    [Key]
    public string Id { get; set; }
    public string Path { get; set; }
    public long ContentLength { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public string ContainerId { get; set; }
    public ContainerDbEntry Container { get; set; }
}
