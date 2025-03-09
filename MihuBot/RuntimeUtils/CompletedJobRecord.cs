using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MihuBot.RuntimeUtils;

[Table("completedRuntimeUtilsJobs")]
public sealed class CompletedJobDbEntry
{
    [Key]
    public string ExternalId { get; set; }
    public string Title { get; set; }
    public DateTime StartedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string RecordJson { get; set; }

    public CompletedJobRecord ToRecord() => JsonSerializer.Deserialize<CompletedJobRecord>(RecordJson);
}

public sealed class CompletedJobRecord
{
    public string ExternalId { get; set; }
    public string Title { get; set; }
    public DateTime StartedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string TestedPROrBranchLink { get; set; }
    public string TrackingIssueUrl { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
    public Artifact[] Artifacts { get; set; }

    [JsonIgnore]
    public string CustomArguments => Metadata.TryGetValue("CustomArguments", out var value) ? value : null;

    [JsonIgnore]
    public string LogsArtifactUrl => Artifacts?.FirstOrDefault(a => a.FileName == "logs.txt")?.Url;

    public CompletedJobDbEntry ToDbEntry() => new()
    {
        ExternalId = ExternalId,
        Title = Title,
        StartedAt = StartedAt,
        Duration = Duration,
        RecordJson = JsonSerializer.Serialize(this),
    };

    public record Artifact(string FileName, string Url, long Size);
}
