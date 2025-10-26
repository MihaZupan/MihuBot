#nullable enable

namespace MihuBot.RuntimeUtils.Search;

public sealed record IssueSearchFilters
{
    public bool IncludeOpen { get; set; } = true;

    public bool IncludeClosed { get; set; } = true;

    public bool IncludeIssues { get; set; } = true;

    public bool IncludePullRequests { get; set; } = true;

    public float MinScore { get; set; } = 0.2f;

    public DateTime? CreatedAfter { get; set; }

    public string? Repository { get; set; }

    public string? LabelContains { get; set; }

    public Func<IssueSearchResult, bool>? PostFilter { get; set; }

    public override string ToString()
    {
        string s = $"{nameof(IncludeOpen)}={IncludeOpen}, {nameof(IncludeClosed)}={IncludeClosed}, {nameof(IncludeIssues)}={IncludeIssues}, {nameof(IncludePullRequests)}={IncludePullRequests}";

        if (CreatedAfter.HasValue)
        {
            s += $", {nameof(CreatedAfter)}={CreatedAfter.Value.ToISODate()}";
        }

        if (!string.IsNullOrEmpty(Repository))
        {
            s += $", {nameof(Repository)}={Repository}";
        }

        if (!string.IsNullOrEmpty(LabelContains))
        {
            s += $", {nameof(LabelContains)}={LabelContains}";
        }

        return s;
    }
}
