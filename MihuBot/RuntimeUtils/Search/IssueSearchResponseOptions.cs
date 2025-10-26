namespace MihuBot.RuntimeUtils.Search;

public sealed record IssueSearchResponseOptions
{
    public int MaxResults { get; set; } = 10;

    public bool PreferSpeed { get; set; } = true;

    public bool IncludeIssueComments { get; set; }
}
