using System.Text.Json;
using System.Text.Json.Serialization;
using Octokit;

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

#nullable enable

public static class GitHubGraphQL
{
    internal const int ReferencedLabelItemsBatchSize = 20;

    internal static async Task<(LinkedItemLabelInfoModel[] Items, int Calls, int Cost)> GetReferencedLabelItemsAsync(
        this GithubGraphQLClient client, (string Repository, int Number)[] references, Action<string> debugLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(references.Length, ReferencedLabelItemsBatchSize);

        if (references.Length == 0)
        {
            return ([], 0, 0);
        }

        Dictionary<string, object> variables = [];
        string[] aliases = [.. Enumerable.Range(0, references.Length).Select(i => $"reference{i}")];

        for (int i = 0; i < references.Length; i++)
        {
            if (!GitHubHelper.TryParseRepoOwnerAndName(references[i].Repository, out string? owner, out string? name, out string[]? extra) ||
                extra.Length != 0)
            {
                throw new ArgumentException("Referenced repositories must be in owner/name form.", nameof(references));
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(references[i].Number);
            variables[$"owner{i}"] = owner;
            variables[$"name{i}"] = name;
            variables[$"number{i}"] = references[i].Number;
        }

        var response = await client.RunAliasedQueryAsync<ReferencedLabelRepositoryModel>(
            Queries.ReferencedLabelItems(references.Length), variables, aliases, cancellationToken);
        List<LinkedItemLabelInfoModel> items = [];

        for (int i = 0; i < references.Length; i++)
        {
            var field = response.Fields[aliases[i]];
            string reference = $"{references[i].Repository}#{references[i].Number}";

            if (field.InvalidData is not null ||
                field.Errors.Any(e => e.RootField != aliases[i] || e.Type is not ("NOT_FOUND" or "FORBIDDEN")))
            {
                throw new InvalidOperationException(field.InvalidData ?? $"GitHub referenced item lookup failed: {string.Join("; ", field.Errors.Select(e => e.Message))}");
            }

            if (field.Errors.Length > 0 || field.Data?.Item is null)
            {
                debugLog($"Referenced item {reference} is unavailable or was not found.");
                continue;
            }

            if (field.Data.IsPrivate || field.Data.Item.Repository.IsPrivate)
            {
                debugLog($"Skipping private referenced item {reference}.");
                continue;
            }

            items.Add(field.Data.Item);
        }

        if (response.RateLimit is null || response.RateLimitErrors.Length > 0)
        {
            throw new InvalidOperationException($"GitHub referenced item lookup returned no usable rate-limit metadata: {string.Join("; ", response.RateLimitErrors)}");
        }

        return ([.. items.DistinctBy(i => i.Url, StringComparer.OrdinalIgnoreCase)], 1, response.RateLimit.Cost);
    }

    internal static async Task<(PullRequestLabelRepositoryModel? Repository, int Calls, int Cost)> GetPullRequestLabelInfoAsync(
        this GithubGraphQLClient client, string owner, string name, int number, CancellationToken cancellationToken = default)
    {
        var response = await client.RunQueryAsync<PullRequestLabelResponseModel>(
            Queries.PullRequestLabelInfo, new { owner, name, number }, cancellationToken);
        return (response.Repository, 1, response.RateLimit?.Cost
            ?? throw new InvalidOperationException("GitHub pull request lookup returned no usable rate-limit metadata."));
    }

    internal static async Task<((string Path, PullRequestHistoryModel PullRequest)[] Matches, int Calls, int Cost)> GetPullRequestFileHistoryAsync(
        this GithubGraphQLClient client, string owner, string name, string baseOid, string[] paths, CancellationToken cancellationToken = default)
    {
        if (paths.Length == 0)
        {
            return ([], 0, 0);
        }

        Dictionary<string, object> variables = new() { ["owner"] = owner, ["name"] = name, ["base"] = baseOid };

        for (int i = 0; i < paths.Length; i++)
        {
            variables[$"path{i}"] = paths[i];
        }

        string[] aliases = [.. Enumerable.Range(0, paths.Length).Select(i => $"path{i}")];
        var response = await client.RunAliasedQueryAsync<PullRequestHistoryRepositoryModel>(
            Queries.PullRequestFileHistory(paths.Length), variables, aliases, cancellationToken);
        List<(string Path, PullRequestHistoryModel PullRequest)> results = [];

        for (int i = 0; i < paths.Length; i++)
        {
            var field = response.Fields[aliases[i]];

            if (field.InvalidData is not null || field.Errors.Length > 0)
            {
                throw new InvalidOperationException(field.InvalidData ?? $"GitHub pull request history lookup failed: {string.Join("; ", field.Errors.Select(e => e.Message))}");
            }

            var commits = field.Data?.Object?.History.Nodes
                ?? throw new InvalidOperationException("GitHub returned no base commit for pull request file history.");

            foreach (var commit in commits)
            {
                results.AddRange(commit.AssociatedPullRequests.Nodes.Select(pr => (paths[i], pr)));
            }
        }

        if (response.RateLimit is null || response.RateLimitErrors.Length > 0)
        {
            throw new InvalidOperationException($"GitHub pull request history lookup returned no usable rate-limit metadata: {string.Join("; ", response.RateLimitErrors)}");
        }

        return ([.. results], 1, response.RateLimit.Cost);
    }

    public static async Task<(string ClosedIssue, string DuplicatedAgainst)[]> GetIssuesMarkedAsDuplicateAsync(this GithubGraphQLClient client, string owner, string name, int count, CancellationToken cancellationToken = default)
    {
        var results = new List<(string ClosedIssue, string DuplicatedAgainst)>();
        string? cursor = null;

        while (results.Count < count)
        {
            var response = await client.RunQueryAsync<DuplicateIssuesResponseModel>(Queries.IssuesMarkedAsDuplicate, new { Owner = owner, Name = name, Count = 100, Cursor = cursor }, cancellationToken);

            var page = response.Repository.Issues;

            foreach (var node in page.Nodes)
            {
                if (node.TimelineItems.Nodes is [var duplicateEvent, ..] &&
                    duplicateEvent.Duplicate is not null &&
                    duplicateEvent.Canonical?.Url == node.Url)
                {
                    results.Add((duplicateEvent.Duplicate.Url, node.Url));

                    if (results.Count >= count)
                    {
                        break;
                    }
                }
            }

            if (!page.PageInfo.HasNextPage)
            {
                break;
            }

            cursor = page.PageInfo.EndCursor;
        }

        return [.. results];
    }

    public static async Task<(ConnectionModel<IssueModel> Issues, int Calls, int Cost)> GetIssuesAndComments(this GithubGraphQLClient client, string owner, string name, string issueCursor, CancellationToken cancellationToken = default)
    {
        var response = await client.RunQueryAsync<RepositoryWithCostModel>(Queries.IssuesAndComments, new { Owner = owner, Name = name, IssueCursor = issueCursor }, cancellationToken);

        var issues = response.Repository.Issues.Nodes;
        int totalCost = response.RateLimit.Cost;
        int calls = 0;

        for (int i = 0; i < issues.Length; i++)
        {
            IssueModel issue = issues[i];

            while (issue.Comments.PageInfo.HasNextPage)
            {
                var moreComments = await client.RunQueryAsync<CommentsNodeWithCostModel>(Queries.MoreNodeComments, new { NodeId = issue.Id, CommentCursor = issue.Comments.PageInfo.EndCursor }, cancellationToken);
                totalCost += moreComments.RateLimit.Cost;
                calls++;

                issue = issue with
                {
                    Comments = new ConnectionModel<CommentModel>(
                        [.. issue.Comments.Nodes, .. moreComments.Node.Comments.Nodes],
                        moreComments.Node.Comments.PageInfo)
                };
            }

            issues[i] = issue;
        }

        return (response.Repository.Issues, calls, totalCost);
    }

    public static async Task<(ConnectionModel<PullRequestModel> PullRequests, int Calls, int Cost)> GetPullRequestsAndComments(this GithubGraphQLClient client, string owner, string name, string pullRequestCursor, CancellationToken cancellationToken = default)
    {
        var response = await client.RunQueryAsync<RepositoryWithCostModel>(Queries.PullRequestsAndComments, new { Owner = owner, Name = name, PullRequestCursor = pullRequestCursor }, cancellationToken);

        var pullRequests = response.Repository.PullRequests.Nodes;
        int totalCost = response.RateLimit.Cost;
        int calls = 0;

        for (int i = 0; i < pullRequests.Length; i++)
        {
            PullRequestModel pullRequest = pullRequests[i];

            while (pullRequest.Comments.PageInfo.HasNextPage)
            {
                var moreComments = await client.RunQueryAsync<CommentsNodeWithCostModel>(Queries.MoreNodeComments, new { NodeId = pullRequest.Id, CommentCursor = pullRequest.Comments.PageInfo.EndCursor }, cancellationToken);
                totalCost += moreComments.RateLimit.Cost;
                calls++;

                pullRequest = pullRequest with
                {
                    Comments = new ConnectionModel<CommentModel>(
                        [.. pullRequest.Comments.Nodes, .. moreComments.Node.Comments.Nodes],
                        moreComments.Node.Comments.PageInfo)
                };
            }

            while (pullRequest.Reviews.PageInfo.HasNextPage)
            {
                var moreComments = await client.RunQueryAsync<ReviewsNodeWithCostModel>(Queries.PullRequestReviewComments, new { NodeId = pullRequest.Id, CommentCursor = pullRequest.Reviews.PageInfo.EndCursor }, cancellationToken);
                totalCost += moreComments.RateLimit.Cost;
                calls++;

                pullRequest = pullRequest with
                {
                    Reviews = new ConnectionModel<PullRequestReviewModel>(
                        [.. pullRequest.Reviews.Nodes, .. moreComments.Node.Reviews.Nodes],
                        moreComments.Node.Reviews.PageInfo)
                };
            }

            PullRequestReviewModel[] reviews = pullRequest.Reviews.Nodes;

            for (int j = 0; j < reviews.Length; j++)
            {
                PullRequestReviewModel review = reviews[j];

                while (review.Comments.PageInfo.HasNextPage)
                {
                    var moreComments = await client.RunQueryAsync<CommentsNodeWithCostModel>(Queries.MoreNodeComments, new { NodeId = review.Id, CommentCursor = review.Comments.PageInfo.EndCursor }, cancellationToken);
                    totalCost += moreComments.RateLimit.Cost;
                    calls++;

                    review = review with
                    {
                        Comments = new ConnectionModel<CommentModel>(
                            [.. review.Comments.Nodes, .. moreComments.Node.Comments.Nodes],
                            moreComments.Node.Comments.PageInfo)
                    };
                }

                reviews[j] = review;
            }

            pullRequests[i] = pullRequest;
        }

        return (response.Repository.PullRequests, calls, totalCost);
    }

    public static async Task<(ConnectionModel<DiscussionModel> Discussions, int Calls, int Cost)> GetDiscussionsAndComments(this GithubGraphQLClient client, string owner, string name, string discussionCursor, CancellationToken cancellationToken = default)
    {
        var response = await client.RunQueryAsync<RepositoryWithCostModel>(Queries.DiscussionsAndComments, new { Owner = owner, Name = name, DiscussionCursor = discussionCursor }, cancellationToken);

        var discussions = response.Repository.Discussions.Nodes;
        int totalCost = response.RateLimit.Cost;
        int calls = 0;

        for (int i = 0; i < discussions.Length; i++)
        {
            DiscussionModel discussion = discussions[i];

            while (discussion.Comments.PageInfo.HasNextPage)
            {
                var moreComments = await client.RunQueryAsync<DiscussionCommentsNodeWithCostModel>(Queries.DiscussionTopLevelComments, new { NodeId = discussion.Id, CommentCursor = discussion.Comments.PageInfo.EndCursor }, cancellationToken);
                totalCost += moreComments.RateLimit.Cost;
                calls++;

                discussion = discussion with
                {
                    Comments = new ConnectionModel<DiscussionCommentModel>(
                        [.. discussion.Comments.Nodes, .. moreComments.Node.Comments.Nodes],
                        moreComments.Node.Comments.PageInfo)
                };
            }

            DiscussionCommentModel[] comments = discussion.Comments.Nodes;

            for (int j = 0; j < comments.Length; j++)
            {
                DiscussionCommentModel comment = comments[j];

                while (comment.Replies.PageInfo.HasNextPage)
                {
                    var moreComments = await client.RunQueryAsync<RepliesNodeWithCostModel>(Queries.MoreNodeComments, new { NodeId = comment.Id, CommentCursor = comment.Replies.PageInfo.EndCursor }, cancellationToken);
                    totalCost += moreComments.RateLimit.Cost;
                    calls++;

                    comment = comment with
                    {
                        Replies = new ConnectionModel<CommentModel>(
                            [.. comment.Replies.Nodes, .. moreComments.Node.Replies.Nodes],
                            moreComments.Node.Replies.PageInfo)
                    };
                }

                comments[j] = comment;
            }

            discussions[i] = discussion;
        }

        return (response.Repository.Discussions, calls, totalCost);
    }

    public static async Task<(UserModel[] Users, int Calls, int Cost)> GetUsers(this GithubGraphQLClient client, string[] logins, CancellationToken cancellationToken = default)
    {
        if (logins.Length == 0)
        {
            return ([], 0, 0);
        }

        string[] aliases = [.. Enumerable.Range(0, logins.Length).Select(i => $"User{i}")];
        var variables = logins.Select((login, i) => KeyValuePair.Create($"login{i}", login)).ToDictionary();
        var response = await client.RunAliasedQueryAsync<UserModel>(Queries.BulkUsers(logins.Length), variables, aliases, cancellationToken);

        UserModel[] users = new UserModel[logins.Length];

        for (int i = 0; i < users.Length; i++)
        {
            var field = response.Fields[aliases[i]];

            if (field.InvalidData is not null || field.Errors.Any(e => e.Type != "NOT_FOUND" || e.RootField != aliases[i]))
            {
                throw new InvalidOperationException(field.InvalidData ?? $"GitHub user lookup failed: {string.Join("; ", field.Errors.Select(e => e.Message))}");
            }

            // Missing users are resolved by ID through REST by the ingestion service.
            users[i] = field.Data!;
        }

        if (response.RateLimit is null || response.RateLimitErrors.Length > 0)
        {
            throw new InvalidOperationException($"GitHub user lookup returned no usable rate-limit metadata: {string.Join("; ", response.RateLimitErrors)}");
        }

        return (users, 1, response.RateLimit.Cost);
    }

    // Large batches can silently truncate GitHub's timeline preloads, even reporting hasNextPage=false.
    internal const int LabelTimelineBatchSize = 25;

    internal static async Task<(LabelTimelineResult[] Timelines, int Calls, int? Cost, GithubGraphQLClient.RateLimitInfo? LastRateLimit)> GetIssueLabelTimelinesAsync(
        this GithubGraphQLClient client, string[] nodeIds, Action<string> debugLog, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nodeIds.Length, LabelTimelineBatchSize);

        LabelTimelineRequest[] all = [.. nodeIds.Select((id, i) => new LabelTimelineRequest(id, $"issue{i}"))];

        foreach (var issue in all.Where(i => string.IsNullOrEmpty(i.Id)))
        {
            issue.Result.Error = "Issue has no GitHub node ID; timeline unavailable.";
        }

        List<LabelTimelineRequest> pending = [.. all.Where(i => i.Result.Error is null)];
        int calls = 0;
        int? totalCost = 0;
        GithubGraphQLClient.RateLimitInfo? lastRateLimit = null;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<string, object?> variables = [];

            foreach (var issue in pending)
            {
                variables.Add($"{issue.Alias}Id", issue.Id);
                variables.Add($"{issue.Alias}Cursor", issue.Cursor);
            }

            string[] aliases = [.. pending.Select(i => i.Alias)];
            GithubGraphQLClient.AliasedResponse<LabelTimelineNode> response;
            calls++;
            lastRateLimit = null;

            try
            {
                response = await client.RunAliasedQueryAsync<LabelTimelineNode>(Queries.LabelTimelines(aliases), variables, aliases, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                totalCost = null;

                foreach (var issue in pending)
                {
                    issue.Result.Error = $"GraphQL timeline request failed: {ex.Message}";
                }

                break;
            }

            totalCost += response.RateLimit?.Cost;
            lastRateLimit = response.RateLimit;

            if (response.RateLimit is not null)
            {
                debugLog($"Label timeline GraphQL request for {pending.Count} issues: {JsonSerializer.Serialize(response.RateLimit, JsonSerializerOptions.Web)}");
            }

            foreach (string error in response.RateLimitErrors)
            {
                debugLog($"Label timeline GraphQL rate-limit metadata failed: {error}");
            }

            List<LabelTimelineRequest> next = [];

            foreach (var issue in pending)
            {
                var field = response.Fields[issue.Alias];

                if (field.Errors.Length > 0)
                {
                    issue.Result.Error = $"GraphQL timeline error: {string.Join("; ", field.Errors.Select(e => e.Message))}";
                    continue;
                }

                try
                {
                    if (field.InvalidData is not null)
                    {
                        throw new InvalidOperationException(field.InvalidData);
                    }

                    if (field.Data is not { } node || node.Id != issue.Id)
                    {
                        throw new InvalidOperationException("GraphQL returned a missing or mismatched issue.");
                    }

                    if (node.TimelineItems is not { Nodes: not null, PageInfo: not null } timeline)
                    {
                        throw new InvalidOperationException("GraphQL returned an incomplete timeline connection.");
                    }

                    foreach (var item in timeline.Nodes)
                    {
                        if (item is null || string.IsNullOrEmpty(item.Id) || !issue.EventIds.Add(item.Id))
                        {
                            throw new InvalidOperationException("GraphQL returned a missing or repeated timeline event ID.");
                        }

                        if (item.Type is not ("LabeledEvent" or "UnlabeledEvent"))
                        {
                            throw new InvalidOperationException($"Unexpected timeline event type: {item.Type}");
                        }

                        if (string.IsNullOrEmpty(item.Label?.Name))
                        {
                            throw new InvalidOperationException("Timeline event has no label name.");
                        }

                        issue.Result.Events.Add(item);
                    }

                    if (timeline.PageInfo.HasNextPage)
                    {
                        string cursor = timeline.PageInfo.EndCursor;

                        if (string.IsNullOrEmpty(cursor) || !issue.Cursors.Add(cursor))
                        {
                            throw new InvalidOperationException("GraphQL returned a missing or repeated timeline cursor.");
                        }

                        issue.Cursor = cursor;
                        next.Add(issue);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    issue.Result.Error = $"Invalid GraphQL timeline: {ex.Message}";
                }
            }

            pending = next;
        }

        return ([.. all.Select(i => i.Result)], calls, totalCost, lastRateLimit);
    }

    private sealed class LabelTimelineRequest(string id, string alias)
    {
        public string Id { get; } = id;
        public string Alias { get; } = alias;
        public string? Cursor { get; set; }
        public HashSet<string> Cursors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EventIds { get; } = new(StringComparer.Ordinal);
        public LabelTimelineResult Result { get; } = new();
    }

    internal sealed class LabelTimelineResult
    {
        public List<LabelTimelineEvent> Events { get; } = [];
        public string? Error { get; set; }
    }

    private sealed record LabelTimelineNode(string? Id, ConnectionModel<LabelTimelineEvent>? TimelineItems);

    internal sealed record LabelNameModel(string? Name);

    internal sealed record LabelTimelineEvent(
        string? Id,
        [property: JsonPropertyName("__typename")] string? Type,
        [property: JsonRequired] DateTimeOffset CreatedAt,
        [property: JsonRequired] ActorIdsModel? Actor,
        LabelNameModel? Label);

    private static class Queries
    {
        private const string BasePageSize = "25";
        private const string SecondaryPageSize = "50";
        private const string ReviewCommentsSize = "20";
        private const string DiscussionCommentRepliesSize = "20";
        private const string AssigneesPerIssue = "20";

        private const string LinkedItemLabelProperties = """
            url title body author { login } repository { nameWithOwner isPrivate }
            labels(first: 100) { nodes { name } }
            """;

        public const string PullRequestLabelInfo = $$"""
            query PullRequestLabelInfo($owner: String!, $name: String!, $number: Int!) {
              rateLimit { cost }
              repository(owner: $owner, name: $name) {
                isPrivate
                pullRequest(number: $number) {
                  id databaseId: fullDatabaseId url title body author { login __typename } baseRefOid isDraft changedFiles additions deletions
                  headRefName baseRefName headRepository { nameWithOwner isPrivate }
                  comments(last: 20) { nodes { body author { login } createdAt } }
                  closingIssuesReferences(first: 20) {
                    nodes {
                      {{LinkedItemLabelProperties}}
                    }
                  }
                }
              }
            }
            """;

        public static string ReferencedLabelItems(int count)
        {
            var query = new StringBuilder("query ReferencedLabelItems(");
            query.AppendJoin(", ", Enumerable.Range(0, count).Select(i => $"$owner{i}: String!, $name{i}: String!, $number{i}: Int!"));
            query.Append(") { rateLimit { cost }");

            for (int i = 0; i < count; i++)
            {
                query.Append($$"""
                    reference{{i}}: repository(owner: $owner{{i}}, name: $name{{i}}) {
                      isPrivate
                      item: issueOrPullRequest(number: $number{{i}}) {
                        ... on Issue { {{LinkedItemLabelProperties}} }
                        ... on PullRequest { {{LinkedItemLabelProperties}} }
                      }
                    }
                    """);
            }

            query.Append('}');
            return query.ToString();
        }

        public static string PullRequestFileHistory(int count)
        {
            var query = new StringBuilder("query PullRequestFileHistory($owner: String!, $name: String!, $base: GitObjectID!");

            for (int i = 0; i < count; i++)
            {
                query.Append($", $path{i}: String!");
            }

            query.Append(") { rateLimit { cost }");

            for (int i = 0; i < count; i++)
            {
                query.Append($$"""
                    path{{i}}: repository(owner: $owner, name: $name) {
                      object(oid: $base) { ... on Commit {
                        history(first: 8, path: $path{{i}}) {
                          nodes { associatedPullRequests(first: 5) { nodes {
                            url title body mergedAt author { login }
                            repository { nameWithOwner isPrivate }
                            labels(first: 100) { nodes { name } }
                          } } }
                        }
                      } }
                    }
                    """);
            }

            query.Append('}');
            return query.ToString();
        }

        private const string PageInfo =
            """
            pageInfo {
              hasNextPage
              endCursor
            }
            """;

        private const string Assignees =
            $$"""
            assignees(first: {{AssigneesPerIssue}}) {
              nodes {
                login
                id
                databaseId
              }
            }
            """;

        private const string Milestone =
            """
            milestone {
              id
            }
            """;

        private static string CommentProperties(string databaseIdName) =>
            $$"""
            nodes {
              url
              ... CommentInfo
              ... ReactionsInfo
              isMinimized
              minimizedReason
              {{databaseIdName}}
            }
            {{PageInfo}}
            """;

        private const string IssueOrPullRequestOrDiscussionProperties =
            $$"""
            id
            url
            number
            title
            body
            createdAt
            updatedAt
            closedAt
            locked
            activeLockReason
            author {
              ... ActorIds
            }
            authorAssociation
            ... LabelsInfo
            ... ReactionsInfo
            """;

        public static readonly string MoreNodeComments =
            $$"""
            query MoreNodeComments(
              $nodeId: ID!,
              $commentCursor: String!)
            {
              rateLimit {
                cost
              }
              node (id: $nodeId) {
                ... on Issue {
                  comments(first: {{SecondaryPageSize}}, after: $commentCursor) {
                    {{CommentProperties("databaseId")}}
                  }
                }
                ... on PullRequest {
                  comments(first: {{SecondaryPageSize}}, after: $commentCursor) {
                    {{CommentProperties("databaseId")}}
                  }
                }
                ... on PullRequestReview {
                  comments(first: {{SecondaryPageSize}}, after: $commentCursor) {
                    {{CommentProperties("fullDatabaseId")}}
                  }
                }
                ... on DiscussionComment {
                  replies(first: {{SecondaryPageSize}}, after: $commentCursor) {
                    {{CommentProperties("databaseId")}}
                  }
                }
              }
            }

            {{Fragments.ActorIds}}
            {{Fragments.CommentInfo}}
            {{Fragments.ReactionsInfo}}
            """;

        public static readonly string PullRequestReviewComments =
            $$"""
            query IssueOrPullRequestComments(
              $nodeId: ID!,
              $commentCursor: String!)
            {
              rateLimit {
                cost
              }
              node (id: $nodeId) {
                ... on PullRequest {
                  reviews(first: {{SecondaryPageSize}}, after: $commentCursor) {
                    nodes {
                      url
                      ... CommentInfo
                      ... ReactionsInfo
                      isMinimized
                      minimizedReason
                      comments(first: {{SecondaryPageSize}}) {
                        {{CommentProperties("fullDatabaseId")}}
                      }
                    }
                    {{PageInfo}}
                  }
                }
              }
            }

            {{Fragments.ActorIds}}
            {{Fragments.CommentInfo}}
            {{Fragments.ReactionsInfo}}
            """;

        public static readonly string IssuesAndComments =
            $$"""
            query IssuesAndComments(
              $owner: String!,
              $name: String!,
              $issueCursor: String!)
            {
              rateLimit {
                cost
              }
              repository(owner: $owner, name: $name) {
                issues(first: {{BasePageSize}}, after: $issueCursor, orderBy: { field: CREATED_AT, direction: ASC }) {
                  nodes {
                    {{IssueOrPullRequestOrDiscussionProperties}}
                    comments(first: {{SecondaryPageSize}}) {
                      {{CommentProperties("databaseId")}}
                    }
                    {{Assignees}}
                    {{Milestone}}
                    state
                  }
                  {{PageInfo}}
                }
              }
            }

            {{Fragments.ActorIds}}
            {{Fragments.LabelsInfo}}
            {{Fragments.CommentInfo}}
            {{Fragments.ReactionsInfo}}
            """;

        public static readonly string PullRequestsAndComments =
            $$"""
            query PullRequestsAndComments(
              $owner: String!,
              $name: String!,
              $pullRequestCursor: String!)
            {
              rateLimit {
                cost
              }
              repository(owner: $owner, name: $name) {
                pullRequests(first: {{BasePageSize}}, after: $pullRequestCursor, orderBy: { field: CREATED_AT, direction: ASC }) {
                  {{PageInfo}}
                  nodes {
                    {{IssueOrPullRequestOrDiscussionProperties}}
                    comments(first: {{SecondaryPageSize}}) {
                      {{CommentProperties("databaseId")}}
                    }
                    {{Assignees}}
                    {{Milestone}}
                    state
                    reviews(first: {{SecondaryPageSize}}) {
                      nodes {
                        url
                        ... CommentInfo
                        ... ReactionsInfo
                        isMinimized
                        minimizedReason
                        comments(first: {{ReviewCommentsSize}}) {
                          {{CommentProperties("fullDatabaseId")}}
                        }
                      }
                      {{PageInfo}}
                    }
                    mergedAt
                    isDraft
                    mergeable
                    additions
                    deletions
                    changedFiles
                    maintainerCanModify
                    mergedBy {
                      ... ActorIds
                    }
                  }
                }
              }
            }

            {{Fragments.ActorIds}}
            {{Fragments.LabelsInfo}}
            {{Fragments.CommentInfo}}
            {{Fragments.ReactionsInfo}}
            """;

        public static readonly string DiscussionsAndComments =
            $$"""
            query DiscussionsAndComments(
              $owner: String!,
              $name: String!,
              $discussionCursor: String!)
            {
              rateLimit {
                cost
              }
              repository(owner: $owner, name: $name) {
                discussions(first: {{BasePageSize}}, after: $discussionCursor, orderBy: { field: CREATED_AT, direction: ASC }) {
                  {{PageInfo}}
                  nodes {
                    {{IssueOrPullRequestOrDiscussionProperties}}
                    upvoteCount
                    isAnswered
                    comments(first: {{SecondaryPageSize}}) {
                      {{CommentProperties("databaseId")}}
                    }
                    comments(first: {{SecondaryPageSize}}) {
                      nodes {
                        url
                        ... CommentInfo
                        ... ReactionsInfo
                        isMinimized
                        minimizedReason
                        replies(first: {{DiscussionCommentRepliesSize}}) {
                          {{CommentProperties("databaseId")}}
                        }
                        upvoteCount
                      }
                      {{PageInfo}}
                    }
                  }
                }
              }
            }

            {{Fragments.ActorIds}}
            {{Fragments.LabelsInfo}}
            {{Fragments.CommentInfo}}
            {{Fragments.ReactionsInfo}}
            """;

        public static readonly string DiscussionTopLevelComments =
            $$"""
            query DiscussionTopLevelComments(
              $nodeId: ID!,
              $commentCursor: String!)
            {
              rateLimit {
                cost
              }
              node (id: $nodeId) {
                ... on Discussion {
                  comments(first: {{SecondaryPageSize}}, after: $commentCursor) {
                    nodes {
                      url
                      ... CommentInfo
                      ... ReactionsInfo
                      isMinimized
                      minimizedReason
                      replies(first: {{SecondaryPageSize}}) {
                        {{CommentProperties("databaseId")}}
                      }
                      upvoteCount
                    }
                    {{PageInfo}}
                  }
                }
              }
            }

            {{Fragments.ActorIds}}
            {{Fragments.CommentInfo}}
            {{Fragments.ReactionsInfo}}
            """;

        public static string BulkUsers(int count) =>
            $$"""
            query BulkUsers({{string.Join(", ", Enumerable.Range(0, count).Select(i => $"$login{i}: String!"))}}) {
              rateLimit {
                cost
              }
              {{string.Join('\n', Enumerable.Range(0, count).Select(i =>
                $$"""
                User{{i}}: user(login: $login{{i}}) { ... UserInfo }
                """))}}
            }

            {{Fragments.UserInfo}}
            """;

        public static string LabelTimelines(string[] aliases) =>
            $$"""
            query({{string.Join(", ", aliases.Select(a => $"${a}Id: ID!, ${a}Cursor: String"))}}) {
              rateLimit { cost remaining resetAt }
              {{string.Join('\n', aliases.Select(alias =>
                $$"""
                {{alias}}: node(id: ${{alias}}Id) {
                  ... on Issue {
                    {{LabelTimelineProperties(alias)}}
                  }
                  ... on PullRequest {
                    {{LabelTimelineProperties(alias)}}
                  }
                }
                """))}}
            }
            {{Fragments.ActorIds}}
            """;

        private static string LabelTimelineProperties(string alias) =>
            $$"""
            id
            timelineItems(first: {{SecondaryPageSize}}, after: ${{alias}}Cursor, itemTypes: [LABELED_EVENT, UNLABELED_EVENT]) {
              nodes {
                __typename
                ... on LabeledEvent { id createdAt actor { ... ActorIds } label { name } }
                ... on UnlabeledEvent { id createdAt actor { ... ActorIds } label { name } }
              }
              {{PageInfo}}
            }
            """;

        public const string IssuesMarkedAsDuplicate =
            $$"""
            query IssuesMarkedAsDuplicate($owner: String!, $name: String!, $count: Int!, $cursor: String) {
              repository(owner: $owner, name: $name) {
                issues(first: $count, after: $cursor, orderBy: { field: CREATED_AT, direction: DESC }) {
                  nodes {
                    url
                    timelineItems(itemTypes: [MARKED_AS_DUPLICATE_EVENT], first: 1) {
                      nodes {
                        ... on MarkedAsDuplicateEvent {
                          canonical {
                            ... on Issue { url }
                            ... on PullRequest { url }
                          }
                          duplicate {
                            ... on Issue { url }
                            ... on PullRequest { url }
                          }
                        }
                      }
                    }
                  }
                  {{PageInfo}}
                }
              }
            }
            """;
    }

    private static class Fragments
    {
        public const string ActorIds =
            """
            fragment ActorIds on Actor {
              __typename
              login
              ... on User {
                id
                databaseId
              }
              ... on Bot {
                id
                databaseId
              }
              ... on Mannequin {
                id
                databaseId
              }
              ... on EnterpriseUserAccount {
                id
              }
              ... on Organization {
                id
                databaseId
              }
            }
            """;

        public const string CommentInfo =
            """
            fragment CommentInfo on Comment {
              id
              body
              createdAt
              updatedAt
              authorAssociation
              author {
                ... ActorIds
              }
            }
            """;

        public const string ReactionsInfo =
            """
            fragment ReactionsInfo on Reactable {
              reactionGroups {
                content
                reactors {
                  totalCount
                }
              }
            }
            """;

        public const string LabelsInfo =
            """
            fragment LabelsInfo on Labelable {
              labels(first: 100) {
                nodes {
                  id
                }
              }
            }
            """;

        public const string UserInfo =
            """
            fragment UserInfo on User {
              id
              login
              databaseId
              name
              url
              company
              location
              bio
              createdAt
              followers {
                totalCount
              }
              following {
                totalCount
              }
            }
            """;
    }

    private sealed record RepositoryWithCostModel(GithubGraphQLClient.RateLimitInfo RateLimit, RepositoryModel Repository);

    internal sealed record NodesModel<T>(T[] Nodes);
    internal sealed record ActorLoginModel(string Login, [property: JsonPropertyName("__typename")] string? Type = null);
    internal sealed record RepositoryIdentityModel(string NameWithOwner, bool IsPrivate);
    internal sealed record PullRequestLabelCommentModel(string Body, ActorLoginModel? Author, DateTime CreatedAt);
    internal sealed record LinkedItemLabelInfoModel(
        string Url, string Title, string Body, ActorLoginModel? Author, RepositoryIdentityModel Repository, NodesModel<LabelNameModel> Labels);
    internal sealed record PullRequestLabelInfoModel(
        string Id, long DatabaseId, string Url, string Title, string Body, ActorLoginModel? Author, string BaseRefOid, bool IsDraft,
        int ChangedFiles, int Additions, int Deletions, NodesModel<PullRequestLabelCommentModel> Comments, NodesModel<LinkedItemLabelInfoModel> ClosingIssuesReferences,
        string? HeadRefName, string? BaseRefName, RepositoryIdentityModel? HeadRepository);
    internal sealed record PullRequestLabelRepositoryModel(bool IsPrivate, PullRequestLabelInfoModel? PullRequest);
    internal sealed record PullRequestHistoryModel(
        string Url, string Title, string Body, ActorLoginModel? Author, RepositoryIdentityModel Repository, NodesModel<LabelNameModel> Labels, DateTime? MergedAt);
    private sealed record PullRequestLabelResponseModel(PullRequestLabelRepositoryModel? Repository, GithubGraphQLClient.RateLimitInfo? RateLimit);
    private sealed record PullRequestHistoryRepositoryModel(PullRequestHistoryCommitModel? Object);
    private sealed record PullRequestHistoryCommitModel(NodesModel<PullRequestAssociatedCommitModel> History);
    private sealed record PullRequestAssociatedCommitModel(NodesModel<PullRequestHistoryModel> AssociatedPullRequests);
    private sealed record ReferencedLabelRepositoryModel([property: JsonRequired] bool IsPrivate, LinkedItemLabelInfoModel? Item);

    private sealed record DuplicateIssuesResponseModel(DuplicateIssuesRepositoryModel Repository);
    private sealed record DuplicateIssuesRepositoryModel(ConnectionModel<DuplicateIssueNode> Issues);
    public sealed record DuplicateIssueNode(string Url, DuplicateTimelineItems TimelineItems);
    public sealed record DuplicateTimelineItems(MarkedAsDuplicateEventModel[] Nodes);
    public sealed record MarkedAsDuplicateEventModel(DuplicateCanonicalModel? Canonical, DuplicateCanonicalModel? Duplicate);
    public sealed record DuplicateCanonicalModel(string Url);

    private sealed record CommentsNodeWithCostModel(GithubGraphQLClient.RateLimitInfo RateLimit, CommentsNode Node);

    private sealed record DiscussionCommentsNodeWithCostModel(GithubGraphQLClient.RateLimitInfo RateLimit, DiscussionCommentsNode Node);

    private sealed record RepliesNodeWithCostModel(GithubGraphQLClient.RateLimitInfo RateLimit, RepliesNode Node);

    private sealed record ReviewsNodeWithCostModel(GithubGraphQLClient.RateLimitInfo RateLimit, ReviewsNode Node);

    private sealed record CommentsNode(ConnectionModel<CommentModel> Comments);

    private sealed record DiscussionCommentsNode(ConnectionModel<DiscussionCommentModel> Comments);

    private sealed record RepliesNode(ConnectionModel<CommentModel> Replies);

    private sealed record ReviewsNode(ConnectionModel<PullRequestReviewModel> Reviews);

    private sealed record RepositoryModel(ConnectionModel<IssueModel> Issues, ConnectionModel<PullRequestModel> PullRequests, ConnectionModel<DiscussionModel> Discussions);

    public sealed record ConnectionModel<T>(T[] Nodes, PageInfo PageInfo);

    public sealed record PageInfo([property: JsonRequired] bool HasNextPage, string EndCursor);

    public sealed record ReactionGroupModel(string Content, TotalCountModel Reactors);

    public sealed record TotalCountModel(int TotalCount);

    public sealed record IdOnlyModel(string Id);

    public sealed record ActorIdsModel(string Login, string Id, int? DatabaseId, [property: JsonPropertyName("__typename")] string? Type = null);

    public sealed record MilestoneModel(string Id);

    public sealed record UserModel(
        string Id,
        string Login,
        long DatabaseId,
        string Name,
        string Url,
        string Company,
        string Location,
        string Bio,
        DateTime CreatedAt,
        TotalCountModel Followers,
        TotalCountModel Following);

    public sealed record IssueModel(
        string Id,
        string Url,
        int Number,
        string Title,
        string Body,
        string State,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? ClosedAt,
        bool Locked,
        string ActiveLockReason,
        string AuthorAssociation,
        ActorIdsModel Author,
        MilestoneModel? Milestone,
        ReactionGroupModel[] ReactionGroups,
        ConnectionModel<ActorIdsModel> Assignees,
        ConnectionModel<IdOnlyModel> Labels,
        ConnectionModel<CommentModel> Comments);

    public sealed record PullRequestModel(
        string Id,
        string Url,
        int Number,
        string Title,
        string Body,
        string State,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? ClosedAt,
        bool Locked,
        string ActiveLockReason,
        string AuthorAssociation,
        ActorIdsModel Author,
        MilestoneModel? Milestone,
        ReactionGroupModel[] ReactionGroups,
        ConnectionModel<ActorIdsModel> Assignees,
        ConnectionModel<IdOnlyModel> Labels,
        ConnectionModel<CommentModel> Comments,
        ConnectionModel<PullRequestReviewModel> Reviews,
        DateTime? MergedAt,
        ActorIdsModel? MergedBy,
        bool IsDraft,
        string Mergeable,
        int Additions,
        int Deletions,
        int ChangedFiles,
        bool MaintainerCanModify)
    {
        public IssueModel AsIssue() => new(
            Id,
            Url,
            Number,
            Title,
            Body,
            State,
            CreatedAt,
            UpdatedAt,
            ClosedAt,
            Locked,
            ActiveLockReason,
            AuthorAssociation,
            Author,
            Milestone,
            ReactionGroups,
            Assignees,
            Labels,
            Comments);
    }

    public sealed record CommentModel(
        string Id,
        string Url,
        string Body,
        long DatabaseId,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        string AuthorAssociation,
        ActorIdsModel Author,
        ReactionGroupModel[] ReactionGroups,
        bool IsMinimized,
        string MinimizedReason);

    public sealed record PullRequestReviewModel(
        string Id,
        string Url,
        string Body,
        long FullDatabaseId,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        string AuthorAssociation,
        ActorIdsModel Author,
        ReactionGroupModel[] ReactionGroups,
        bool IsMinimized,
        string MinimizedReason,
        ConnectionModel<CommentModel> Comments)
    {
        public CommentModel AsComment() => new(
            Id,
            Url,
            Body,
            FullDatabaseId,
            CreatedAt,
            UpdatedAt,
            AuthorAssociation,
            Author,
            ReactionGroups,
            IsMinimized,
            MinimizedReason);
    }

    public sealed record DiscussionModel(
        string Id,
        string Url,
        int Number,
        string Title,
        string Body,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? ClosedAt,
        bool Locked,
        string ActiveLockReason,
        string AuthorAssociation,
        ActorIdsModel Author,
        ReactionGroupModel[] ReactionGroups,
        ConnectionModel<IdOnlyModel> Labels,
        ConnectionModel<DiscussionCommentModel> Comments,
        int UpvoteCount,
        bool? IsAnswered)
    {
        public IssueModel AsIssue() => new(
            Id,
            Url,
            Number,
            Title,
            Body,
            ClosedAt.HasValue ? ItemState.Closed.ToString() : ItemState.Open.ToString(),
            CreatedAt,
            UpdatedAt,
            ClosedAt,
            Locked,
            ActiveLockReason,
            AuthorAssociation,
            Author,
            Milestone: null,
            ReactionGroups,
            Assignees: new ConnectionModel<ActorIdsModel>([], new PageInfo(HasNextPage: false, EndCursor: string.Empty)),
            Labels,
            Comments: new ConnectionModel<CommentModel>([.. Comments.Nodes.Select(c => c.AsComment())], Comments.PageInfo));
    }

    public sealed record DiscussionCommentModel(
        string Id,
        string Url,
        string Body,
        long DatabaseId,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        string AuthorAssociation,
        ActorIdsModel Author,
        ReactionGroupModel[] ReactionGroups,
        bool IsMinimized,
        string MinimizedReason,
        ConnectionModel<CommentModel> Replies,
        int UpvoteCount)
    {
        public CommentModel AsComment() => new(
            Id,
            Url,
            Body,
            DatabaseId,
            CreatedAt,
            UpdatedAt,
            AuthorAssociation,
            Author,
            ReactionGroups,
            IsMinimized,
            MinimizedReason);
    }
}
