#nullable enable

namespace MihuBot.RuntimeUtils.Search;

public sealed record IssueSearchFilters
{
    public bool IncludeOpen { get; set; } = true;

    public bool IncludeClosed { get; set; } = true;

    public bool IncludeIssues { get; set; } = true;

    public bool IncludePullRequests { get; set; } = true;

    public bool? IncludeCommentsInResponse { get; set; }

    public float MinScore { get; set; } = 0.2f;

    public DateTime? CreatedAfter { get; set; }

    public string? Repository { get; set; }

    public string[]? Labels { get; set; }

    public Func<IssueSearchResult, bool>? PostFilter { get; set; }

    public override string ToString()
    {
        string s = $"{nameof(IncludeOpen)}={IncludeOpen}, {nameof(IncludeClosed)}={IncludeClosed}, {nameof(IncludeIssues)}={IncludeIssues}, {nameof(IncludePullRequests)}={IncludePullRequests}";

        s += $", {nameof(MinScore)}={MinScore}, {nameof(IncludeCommentsInResponse)}={IncludeCommentsInResponse}";

        if (CreatedAfter.HasValue)
        {
            s += $", {nameof(CreatedAfter)}={CreatedAfter.Value.ToISODate()}";
        }

        if (!string.IsNullOrEmpty(Repository))
        {
            s += $", {nameof(Repository)}={Repository}";
        }

        if (Labels is not null)
        {
            s += $", {nameof(Labels)}={string.Join(';', Labels)}";
        }

        return s;
    }
}
