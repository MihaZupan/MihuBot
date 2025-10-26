using System.Text.Json.Serialization;
using MihuBot.DB.GitHub;

#nullable enable

namespace MihuBot.RuntimeUtils.Search;

/// <summary>
/// Represents a search result for a single issue and optional comment match
/// </summary>
public sealed record IssueSearchResult
{
    public required double Score { get; init; }
    public required IssueInfo Issue { get; init; }
    public required CommentInfo? Comment { get; init; }
}

/// <summary>
/// Represents all matches found for a particular issue, including any relevant comments
/// </summary>
public sealed record IssueResultGroup
{
    public required IList<IssueSearchResult> Results { get; init; } = null!;

    public required double Score { get; init; }

    /// <summary>
    /// The primary issue for this result group
    /// </summary>
    [JsonIgnore]
    public IssueInfo Issue => Results[0].Issue;

    /// <summary>
    /// All comment matches for this issue (excluding null comments)
    /// </summary>
    [JsonIgnore]
    public IList<CommentInfo> Comments => field ??= Results
        .Where(m => m.Comment != null)
        .Select(m => m.Comment!)
        .ToList();
}

/// <summary>
/// Complete search results including all matched issues and performance timing information
/// </summary>
public sealed record GitHubSearchResponse
{
    public static GitHubSearchResponse Empty { get; } = new() { Results = [], Timings = new SearchTimings() };

    public required IList<IssueResultGroup> Results { get; init; }

    public required SearchTimings Timings { get; init; }

    /// <summary>
    /// Number of unique issues found
    /// </summary>
    [JsonIgnore]
    public int TotalIssues => Results.Count;

    /// <summary>
    /// Total number of individual matches (issues + comments)
    /// </summary>
    [JsonIgnore]
    public int TotalMatches => Results.Sum(r => r.Results.Count);
}
