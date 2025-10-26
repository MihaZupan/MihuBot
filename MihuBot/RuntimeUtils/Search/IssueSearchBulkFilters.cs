using MihuBot.DB.GitHub;

#nullable enable

namespace MihuBot.RuntimeUtils.Search;

public sealed record IssueSearchBulkFilters
{
    public const string SearchTermPlaceholder = "{{SearchTerm}}";
    public const string DefaultPostProcessingContext = $"The user searched for '{SearchTermPlaceholder}'.";

    public string? PostProcessingContext { get; set; } = DefaultPostProcessingContext;

    public bool PostProcessIssues { get; set; } = true;

    public IList<IssueInfo>? ExcludeIssues { get; set; }

    public int MaxResultsPerTerm { get; set; } = 20;

    public string GetPostProcessingContext(string searchTerm) =>
        (PostProcessingContext ?? DefaultPostProcessingContext).Replace(SearchTermPlaceholder, searchTerm);

    public override string ToString()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{nameof(PostProcessingContext)}={PostProcessingContext}");

        sb.AppendLine($"{nameof(PostProcessIssues)}={PostProcessIssues}");

        if (ExcludeIssues?.Count > 0)
            sb.AppendLine($"{nameof(ExcludeIssues)}=[{string.Join(", ", ExcludeIssues.Select(e => e.Id))}]");
        else
            sb.AppendLine($"{nameof(ExcludeIssues)}=[]");

        sb.AppendLine($"{nameof(MaxResultsPerTerm)}={MaxResultsPerTerm}");

        return sb.ToString();
    }
}
