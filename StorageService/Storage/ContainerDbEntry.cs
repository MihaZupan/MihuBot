using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace StorageService.Storage;

#nullable disable

[Table("containers")]
public sealed class ContainerDbEntry
{
    [Key]
    public string Name { get; set; }

    public string Owner { get; set; }

    public string SasKey { get; set; }

    public bool IsPublic { get; set; }

    public long RetentionPeriodSeconds { get; set; }
}
