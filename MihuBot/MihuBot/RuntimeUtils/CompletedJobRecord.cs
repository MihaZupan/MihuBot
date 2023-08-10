using System.Text.Json.Serialization;

namespace MihuBot.RuntimeUtils;

public sealed class CompletedJobRecord
{
    public string ExternalId { get; set; }
    public string JobTitle { get; set; }
    public DateTime StartedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string TestedPROrBranchLink { get; set; }
    public string TrackingIssueUrl { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
    public string LogsArtifactUrl { get; set; }
    public JitDiffEntry[] Improvements { get; set; }
    public JitDiffEntry[] Regressions { get; set; }
    public JitDiffEntry[] SizeNeutral { get; set; }

    [JsonIgnore]
    public string CustomArguments => Metadata.TryGetValue("CustomArguments", out var value) ? value : null;
}

public sealed class JitDiffEntry
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string Diff { get; set; }

    [JsonIgnore]
    public bool IsRemovedMethodImprovement => Description.Contains("-100.", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsNewMethodRegression => Description.Contains("∞ of base", StringComparison.Ordinal);
}
