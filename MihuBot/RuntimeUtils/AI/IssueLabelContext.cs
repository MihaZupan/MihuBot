using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using static MihuBot.RuntimeUtils.DataIngestion.GitHub.GitHubGraphQL;

namespace MihuBot.RuntimeUtils.AI;

public sealed class IssueLabelContext(GithubGraphQLClient graphQL, Logger logger)
{
    private readonly Action<string> _debugLog = message => logger.DebugLog(message);

    internal IssueLabelContext(GithubGraphQLClient graphQL, Action<string> debugLog)
        : this(graphQL, (Logger)null)
    {
        _debugLog = debugLog;
    }

    internal async Task<RelatedItem[]> GetMentionedItemsAsync(IssueInfo issue, string[] labels, CancellationToken cancellationToken)
    {
        var (items, calls, cost) = await GetMentionedItemsAsync(issue, labels, issue.Body, issue.HtmlUrl, [], cancellationToken);
        _debugLog($"Mentioned label evidence for <{issue.HtmlUrl}>: {calls} GraphQL API calls, cost {cost}.");
        return items;
    }

    internal async Task<(RelatedItem[] Items, int Calls, int Cost)> GetMentionedItemsAsync(
        IssueInfo issue, string[] labels, string body, string url, IEnumerable<string> excludedUrls, CancellationToken cancellationToken)
    {
        var excluded = new HashSet<string>(excludedUrls, StringComparer.OrdinalIgnoreCase) { url };
        var references = GetDescriptionReferences(body, issue.Repository.FullName, issue.Number, excluded);
        var (items, calls, cost) = await graphQL.GetReferencedLabelItemsAsync(references, _debugLog, cancellationToken);
        return ([.. items
            .Where(i => !excluded.Contains(i.Url))
            .Select(i => CreateRelatedItem(i, issue.Repository.FullName, labels))], calls, cost);
    }

    internal static RelatedItem CreateRelatedItem(LinkedItemLabelInfoModel item, string repository, string[] labels) => new(
        item.Url, (item.Title ?? "").TruncateWithDotDotDot(AreaLabelDetector.MaxRelatedTitleCharacters),
        (item.Body ?? "").TruncateWithDotDotDot(AreaLabelDetector.MaxContextBodyCharacters),
        item.Author?.Login, GetLabels(item.Labels, labels, item.Repository.NameWithOwner.Equals(repository, StringComparison.OrdinalIgnoreCase)));

    internal static (string Repository, int Number)[] GetDescriptionReferences(
        string body, string repository, int number, IEnumerable<string> excludedUrls)
    {
        HashSet<(string Repository, int Number)> seen = [(repository.ToLowerInvariant(), number)];

        foreach (string url in excludedUrls)
        {
            if (GitHubHelper.TryParseIssueOrPRNumber(url, out string excludedRepository, out int excludedNumber) && excludedRepository is not null)
            {
                seen.Add((excludedRepository.ToLowerInvariant(), excludedNumber));
            }
        }

        return [.. GitHubHelper.ExtractIssueOrPullRequestReferences(body, repository)
            .Select(r => (Repository: r.Repository.ToLowerInvariant(), r.Number))
            .Where(seen.Add)
            .Take(ReferencedLabelItemsBatchSize)];
    }

    internal static string[] GetLabels(NodesModel<LabelNameModel> labels, string[] candidates, bool sameRepository) =>
        [.. labels.Nodes.Select(l => l.Name)
            .Where(l => !sameRepository || candidates.Contains(l, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    internal sealed record RelatedItem(string Url, string Title, string Body, string Author, string[] Labels);
}
