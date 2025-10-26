using System.ComponentModel.DataAnnotations.Schema;

#nullable disable

namespace MihuBot.DB.GitHub;

[Table("labels")]
public sealed class LabelInfo
{
    public string Id { get; set; }
    public string Url { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
    public string Description { get; set; }

    public long RepositoryId { get; set; }
    public RepositoryInfo Repository { get; set; }

    public ICollection<IssueInfo> Issues { get; set; }
}
