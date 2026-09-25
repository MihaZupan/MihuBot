using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using static MihuBot.RuntimeUtils.DataIngestion.GitHub.GitHubGraphQL;

namespace MihuBot.RuntimeUtils.AI;

public sealed class IssueLabelContext(
    IDbContextFactory<GitHubDbContext> githubDb, GitHubDataIngestionService dataIngestion, GithubGraphQLClient graphQL, Logger logger)
{
    private readonly Action<string> _debugLog = message => logger.DebugLog(message);
    private readonly Func<(string Repository, int Number)[], CancellationToken, Task<IssueInfo[]>> _getStoredItems;

    internal IssueLabelContext(GithubGraphQLClient graphQL, Action<string> debugLog,
        Func<(string Repository, int Number)[], CancellationToken, Task<IssueInfo[]>> getStoredItems)
        : this(null, null, graphQL, (Logger)null)
    {
        _debugLog = debugLog;
        _getStoredItems = getStoredItems;
    }

    internal async Task<RelatedItem[]> GetMentionedItemsAsync(IssueInfo issue, string[] labels, CancellationToken cancellationToken)
    {
        var (items, calls, cost) = await GetMentionedItemsAsync(issue, labels, issue.Body, issue.HtmlUrl, [], cancellationToken);

        if (calls > 0)
        {
            _debugLog($"Mentioned label evidence for <{issue.HtmlUrl}>: {calls} GraphQL API calls, cost {cost}.");
        }

        return items;
    }

    internal async Task<(RelatedItem[] Items, int Calls, int Cost)> GetMentionedItemsAsync(
        IssueInfo issue, string[] labels, string body, string url, IEnumerable<string> excludedUrls, CancellationToken cancellationToken)
    {
        using var activity = MihuBotAIActivitySource.Instance.StartActivity("GetMentionedLabelContext");
        activity?.SetOperation("context", "mentions");
        activity?.SetTag("issue.number", issue.Number);
        activity?.SetTag("issue.repository", issue.Repository.FullName);

        try
        {
            var excluded = new HashSet<string>(excludedUrls, StringComparer.OrdinalIgnoreCase) { url };
            var references = GetDescriptionReferences(body, issue.Repository.FullName, issue.Number, excluded);
            activity?.SetTag("references.limitReached", references.Length == ReferencedLabelItemsBatchSize);

            if (references.Length == 0)
            {
                activity?.SetSuccess();

                return ([], 0, 0);
            }

            IssueInfo[] stored;
            using (var storedActivity = MihuBotAIActivitySource.Instance.StartActivity("GetStoredLabelReferences"))
            {
                try
                {
                    stored = await (_getStoredItems ?? GetStoredItemsAsync)(references, cancellationToken);
                    storedActivity?.SetSuccess();
                }
                catch (Exception ex)
                {
                    storedActivity?.SetError(ex.GetType().Name);
                    throw;
                }
            }

            var found = stored.Select(i => (i.Repository.FullName.ToLowerInvariant(), i.Number)).ToHashSet();
            var missing = references.Where(r => !found.Contains(r)).ToArray();

            var (items, calls, cost) = await graphQL.GetReferencedLabelItemsAsync(missing, _debugLog, cancellationToken);
            List<LinkedItemLabelInfoModel> localItems = [];

            foreach (var item in stored)
            {
                if (item.Repository.Private)
                {
                    _debugLog($"Skipping private referenced item {item.Repository.FullName}#{item.Number}.");
                    continue;
                }

                localItems.Add(new(item.HtmlUrl, item.Title, item.Body, item.User is null ? null : new(item.User.Login),
                    new(item.Repository.FullName, false), new([.. item.Labels.Select(l => new LabelNameModel(l.Name))])));
            }

            RelatedItem[] results = [.. localItems.Concat(items)
                .Where(i => !excluded.Contains(i.Url))
                .DistinctBy(i => i.Url, StringComparer.OrdinalIgnoreCase)
                .Select(i => CreateRelatedItem(i, issue.Repository.FullName, labels))];
            activity?.SetSuccess();

            return (results, calls, cost);
        }
        catch (Exception ex)
        {
            activity?.SetTag("context.cancelled", cancellationToken.IsCancellationRequested);
            activity?.SetError(ex.GetType().Name);
            throw;
        }
    }

    private async Task<IssueInfo[]> GetStoredItemsAsync((string Repository, int Number)[] references, CancellationToken cancellationToken)
    {
        await using var db = await githubDb.CreateDbContextAsync(cancellationToken);
        List<IssueInfo> items = [];

        foreach (var group in references.GroupBy(r => r.Repository))
        {
            long repositoryId = await dataIngestion.TryGetKnownRepositoryIdAsync(group.Key, cancellationToken);

            if (repositoryId <= 0)
            {
                continue;
            }

            int[] numbers = [.. group.Select(r => r.Number)];
            items.AddRange(await db.Issues
                .AsNoTracking()
                .Where(i => i.RepositoryId == repositoryId && numbers.Contains(i.Number))
                .Include(i => i.Repository)
                .Include(i => i.User)
                .Include(i => i.Labels)
                .AsSplitQuery()
                .ToArrayAsync(cancellationToken));
        }

        return [.. items];
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
