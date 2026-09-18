using System.Globalization;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils.AI;

internal sealed record AreaLabelBacktestRequest(string Repository, int? IssueNumber, int Count, string LabelerActor);

public sealed class AreaLabelBacktestService
{
    private readonly GitHubClient _github;
    private readonly GithubGraphQLClient _graphQL;
    private readonly Func<string, CancellationToken, Task<RepositoryInfo>> _getRepository;
    private readonly Func<RepositoryInfo, IssueInfo, CancellationToken, Task<AreaLabelSuggestion[]>> _predict;
    private readonly Action<string> _debugLog;

    public AreaLabelBacktestService(GitHubClient github, GithubGraphQLClient graphQL, GitHubDataIngestionService ingestion, AreaLabelDetector detector, Logger logger)
        : this(github, graphQL,
            (repo, ct) => ingestion.TryGetRepositoryInfoAsync(
                ingestion.Stats.TrackedRepos.FirstOrDefault(r => r.RepoName.Equals(repo, StringComparison.OrdinalIgnoreCase))?.RepoName ?? repo, ct),
            (repo, issue, ct) => detector.GetSuggestionsAsync(repo, issue, cancellationToken: ct),
            message => logger.DebugLog(message))
    {
    }

    internal AreaLabelBacktestService(
        GitHubClient github,
        GithubGraphQLClient graphQL,
        Func<string, CancellationToken, Task<RepositoryInfo>> getRepository,
        Func<RepositoryInfo, IssueInfo, CancellationToken, Task<AreaLabelSuggestion[]>> predict,
        Action<string> debugLog)
    {
        _github = github;
        _graphQL = graphQL;
        _getRepository = getRepository;
        _predict = predict;
        _debugLog = debugLog;
    }

    internal async Task<AreaLabelBacktestReport> RunAsync(AreaLabelBacktestRequest request, CancellationToken cancellationToken)
    {
        RepositoryInfo repo = await _getRepository(request.Repository, cancellationToken)
            ?? throw new InvalidOperationException($"Repository '{request.Repository}' is not tracked in the GitHub database.");

        if (repo.Private)
        {
            throw new InvalidOperationException("Label evaluation is only available for public repositories.");
        }

        if (AreaLabelDetector.GetCandidateLabels(repo, "area-").Length == 0)
        {
            throw new InvalidOperationException("The repository has no active area-* labels to predict.");
        }

        var report = new AreaLabelBacktestReport(request);
        IReadOnlyList<Issue> issues;

        if (request.IssueNumber is { } number)
        {
            Issue issue = await _github.Issue.Get(repo.Id, number).WaitAsyncAndSupressNotObserved(cancellationToken);

            if (issue.PullRequest is not null)
            {
                throw new InvalidOperationException("Please select an issue, not a pull request.");
            }

            issues = [issue];
        }
        else
        {
            issues = await GetIssuesAsync(repo.Id, request.Count, cancellationToken);
        }

        AreaLabelEvaluation unfinished = null;

        try
        {
            foreach (Issue[] batch in issues.Chunk(GitHubGraphQL.LabelTimelineBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (timelines, _, _, _) = await _graphQL.GetIssueLabelTimelinesAsync([.. batch.Select(i => i.NodeId)], _debugLog, cancellationToken);

                for (int i = 0; i < batch.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Issue issue = batch[i];

                    var evaluation = new AreaLabelEvaluation(
                        issue.Number, issue.HtmlUrl, issue.Title, issue.State.ToString(),
                        AreaLabelHistory.Normalize(issue.Labels.Select(l => l.Name)));

                    report.Issues.Add(evaluation);
                    unfinished = evaluation;

                    if (timelines[i].Error is { } error)
                    {
                        _debugLog($"Failed to fetch label timeline for {issue.HtmlUrl}: {error}");
                        evaluation.Errors.Add($"Timeline failed: {error}");
                    }
                    else
                    {
                        evaluation.History = AreaLabelHistory.AnalyzeEvents(
                            timelines[i].Events.Select(e => AreaLabelEvent.FromGraphQL(e, request.LabelerActor)),
                            evaluation.CurrentLabels, request.LabelerActor);
                    }

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        evaluation.Suggestions = await _predict(repo, CreatePredictionInput(repo, issue), cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _debugLog($"Failed to predict labels for {issue.HtmlUrl}: {ex}");
                        evaluation.Errors.Add($"Prediction failed: {ex.Message}");
                    }

                    unfinished = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report.Cancelled = true;

            unfinished?.Errors.Add("Cancelled before evaluation completed.");
        }

        return report;
    }

    private async Task<IReadOnlyList<Issue>> GetIssuesAsync(long repositoryId, int count, CancellationToken cancellationToken)
    {
        List<Issue> issues = [];
        HashSet<int> seen = [];

        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.All,
            SortProperty = IssueSort.Created,
            SortDirection = Octokit.SortDirection.Descending,
        };

        for (int page = 1; issues.Count < count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await _github.Issue.GetAllForRepository(repositoryId, request,
                new ApiOptions { StartPage = page, PageCount = 1, PageSize = 100 })
                .WaitAsyncAndSupressNotObserved(cancellationToken);

            foreach (Issue issue in batch)
            {
                if (issue.PullRequest is null && seen.Add(issue.Number))
                {
                    issues.Add(issue);

                    if (issues.Count == count)
                    {
                        break;
                    }
                }
            }

            if (batch.Count < 100)
            {
                break;
            }
        }

        return issues;
    }

    internal static IssueInfo CreatePredictionInput(RepositoryInfo repository, Issue issue)
    {
        var input = new IssueInfo
        {
            Repository = repository,
            RepositoryId = repository.Id,
            IssueType = IssueType.Issue,
            UserId = issue.User.Id,
            User = new UserInfo { Id = issue.User.Id, Login = issue.User.Login },
            Labels = [],
            Comments = [],
            Assignees = [],
        };

        GitHubDataIngestionService.PopulateBasicIssueInfo(input, issue);
        // Evaluate the submission, not the subsequent triage discussion or its answer labels.
        input.ClosedAt = null;

        return input;
    }
}

internal sealed class AreaLabelEvaluation(int number, string url, string title, string state, string[] currentLabels)
{
    public int Number { get; } = number;
    public string Url { get; } = url;
    public string Title { get; } = title;
    public string State { get; } = state;
    public string[] CurrentLabels { get; } = currentLabels;
    public string[] CurrentAreas => AreaLabelHistory.Areas(CurrentLabels);
    public AreaLabelSuggestion[] Suggestions { get; set; }
    public string[] Predicted => AreaLabelHistory.Normalize(Suggestions.Select(s => s.LabelName));
    public AreaLabelHistory History { get; set; }
    public List<string> Errors { get; } = [];
}

internal sealed record AreaLabelEvent(DateTimeOffset At, string Actor, bool IsHuman, bool Added, string Label)
{
    internal static AreaLabelEvent FromGraphQL(GitHubGraphQL.LabelTimelineEvent item, string labelerActor)
    {
        var actor = item.Actor;

        bool human = actor is not null && new UserInfo
        {
            Id = actor.DatabaseId ?? 0,
            Login = actor.Login,
            Type = actor.Type == "User" ? AccountType.User : actor.Type == "Bot" ? AccountType.Bot : null,
        }.IsLikelyARealUser() && !actor.Login.Equals(labelerActor, StringComparison.OrdinalIgnoreCase);

        return new(item.CreatedAt, actor?.Login ?? "(unknown)", human, item.Type == "LabeledEvent", item.Label.Name);
    }
}

internal sealed record AreaLabelHistory(
    string Status,
    string[] OriginalLabels,
    bool ObservedLabeler,
    bool Consistent,
    bool HumanChanged,
    AreaLabelEvent[] Events)
{
    internal const string NeedsAreaLabel = "needs-area-label";

    internal static bool IsArea(string label) => label.StartsWith("area-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRelevant(string label) => IsArea(label) || label.Equals(NeedsAreaLabel, StringComparison.OrdinalIgnoreCase);

    internal static string[] Normalize(IEnumerable<string> labels) => labels.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string[] Areas(IEnumerable<string> labels) => Normalize(labels.Where(IsArea));

    internal static bool Equal(IEnumerable<string> left, IEnumerable<string> right) => left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    internal static AreaLabelHistory Analyze(IEnumerable<TimelineEventInfo> timeline, string[] currentLabels, string labelerActor)
    {
        AreaLabelEvent[] events = timeline
            .Where(e => e.Label?.Name is { } name && IsRelevant(name) &&
                e.Event.TryParse(out EventInfoState state) && state is EventInfoState.Labeled or EventInfoState.Unlabeled)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Select(e => new AreaLabelEvent(e.CreatedAt, e.Actor?.Login ?? "(unknown)",
                e.Actor is { } actor && new UserInfo { Id = actor.Id, Login = actor.Login, Type = actor.Type }.IsLikelyARealUser() &&
                    !actor.Login.Equals(labelerActor, StringComparison.OrdinalIgnoreCase),
                e.Event.Value == EventInfoState.Labeled, e.Label.Name))
            .ToArray();

        return AnalyzeEvents(events, currentLabels, labelerActor);
    }

    internal static AreaLabelHistory AnalyzeEvents(IEnumerable<AreaLabelEvent> timeline, string[] currentLabels, string labelerActor)
    {
        // Preserve API order for events sharing a timestamp, including across GraphQL pages.
        AreaLabelEvent[] events = timeline.Where(e => IsRelevant(e.Label)).OrderBy(e => e.At).ToArray();
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        bool consistent = true;

        foreach (AreaLabelEvent e in events)
        {
            if (e.Added)
            {
                consistent &= active.Add(e.Label);
            }
            else
            {
                consistent &= active.Remove(e.Label);
            }
        }

        consistent &= Equal(active, currentLabels.Where(IsRelevant));

        int first = Array.FindIndex(events, e => e.Added && IsLabeler(e));

        if (first < 0)
        {
            bool humanLabeled = events.Any(e => e.Added && e.IsHuman && IsArea(e.Label));

            return new(
                consistent
                    ? humanLabeled ? "No observed labeler application; human labeled (possible existing-label skip)" : "No observed labeler application; reason unknown"
                    : "Timeline inconsistent with current labels; original outcome unknown",
                [], false, consistent, false, events);
        }

        // A timeline does not identify workflow runs. Keep the first consecutive application batch,
        // rather than merging later re-runs or human corrections into the original decision.
        string[] original = events[first].Label.Equals(NeedsAreaLabel, StringComparison.OrdinalIgnoreCase)
            ? [NeedsAreaLabel]
            : Normalize(events.Skip(first).TakeWhile(e => e.Added && IsLabeler(e) && IsArea(e.Label)).Select(e => e.Label));

        bool humanChanged = events.Skip(first + 1).Any(e => e.IsHuman && IsArea(e.Label));
        bool otherChanged = events.Skip(first + 1).Any(e => !IsLabeler(e) && IsArea(e.Label));
        bool fallback = original.Contains(NeedsAreaLabel, StringComparer.OrdinalIgnoreCase) && Areas(original).Length == 0;
        bool same = Equal(Areas(original), Areas(currentLabels));

        string status = fallback ? "Applied needs-area-label (abstained)" :
            same ? "Original area labels match current labels" : "Original area labels differ from current labels";

        if (humanChanged)
        {
            status += same ? "; later human edits (final area set restored)" : "; later human label change";
        }
        else if (otherChanged)
        {
            status += "; later non-human/unknown-actor label change";
        }

        if (!consistent)
        {
            status += "; timeline inconsistent with current labels (unscored baseline)";
        }

        return new(status, original, true, consistent, humanChanged, events);

        bool IsLabeler(AreaLabelEvent e) => e.Actor.Equals(labelerActor, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AreaLabelBacktestReport(AreaLabelBacktestRequest request)
{
    public List<AreaLabelEvaluation> Issues { get; } = [];
    public bool Cancelled { get; set; }
    internal AreaLabelEvaluation[] PredictedIssues => [.. Issues.Where(i => i.Suggestions is not null)];
    internal AreaLabelEvaluation[] CurrentScored => [.. PredictedIssues.Where(i => i.CurrentAreas.Length > 0)];
    internal AreaLabelEvaluation[] OriginalScored => [.. PredictedIssues.Where(i => i.History is { ObservedLabeler: true, Consistent: true })];

    public string Summary =>
        $"{(Cancelled ? "Label evaluation CANCELLED (partial report)" : "Label evaluation")}: {Issues.Count}/{request.Count} issues evaluated, {PredictedIssues.Length} predicted, " +
        $"{Issues.Count(i => i.Errors.Count > 0)} with errors. " +
        $"Exact matches: current area labels {CurrentScored.Count(i => AreaLabelHistory.Equal(i.CurrentAreas, i.Predicted))}/{CurrentScored.Length}; " +
        $"original labeler {OriginalScored.Count(i => AreaLabelHistory.Equal(AreaLabelHistory.Areas(i.History.OriginalLabels), i.Predicted))}/{OriginalScored.Length}. " +
        "Full results and mismatch breakdowns attached. No GitHub labels changed.";

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"Area label evaluation: {request.Repository}");
        text.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        text.AppendLine(Summary);
        text.AppendLine($"Original labeler actor: {request.LabelerActor}");
        text.AppendLine("Sampling: newest issues by creation time, open and closed, excluding pull requests.");
        text.AppendLine("Prediction uses current title/body, without current labels, comments, assignees or milestone. Similar-issue retrieval uses today's indexed data.");
        text.AppendLine("This is a retrospective comparison, NOT an as-of-creation replay: edited bodies and later related issues may influence predictions.");
        text.AppendLine("All detector suggestions are compared as a set (confidence >= 0.5, maximum 5); an empty prediction is abstention.");
        text.AppendLine("Current area labels are a reference, not verified ground truth. Issues without current area labels are unscored against current labels.");
        text.AppendLine("Original labels are the initial needs-area-label fallback or first consecutive area-label additions by the selected actor; fallback is scored as abstention.");
        text.AppendLine("Actor attribution is assumed, not workflow-verified: other workflows may share that actor. No event cannot prove a skipped or unexecuted prediction.");
        text.AppendLine("Human edits include removals/additions/restorations, not necessarily corrections. Missing/deleted/renamed labels or concurrent edits may make a timeline inconsistent.");
        text.AppendLine("Unavailable/inconsistent timelines are excluded only from original-labeler scoring; prediction failures are excluded from prediction-comparison denominators.");
        text.AppendLine();

        text.AppendLine("Original labeler outcomes (all sampled issues):");

        foreach (var group in Issues.GroupBy(i => i.History?.Status ?? "Timeline unavailable").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"  {group.Count()}: {group.Key}");
        }

        AppendComparisons(text, "Current area labels -> prediction", CurrentScored.Select(i => (i.CurrentAreas, i.Predicted)));
        AppendComparisons(text, "Original labeler -> prediction", OriginalScored.Select(i => (AreaLabelHistory.Areas(i.History.OriginalLabels), i.Predicted)));

        AppendComparisons(text, "Current area labels -> original labeler", Issues
            .Where(i => i.CurrentAreas.Length > 0 && i.History is { ObservedLabeler: true, Consistent: true })
            .Select(i => (i.CurrentAreas, AreaLabelHistory.Areas(i.History.OriginalLabels))));

        text.AppendLine();
        text.AppendLine("ALL EVALUATED ISSUES");

        foreach (AreaLabelEvaluation issue in Issues)
        {
            text.AppendLine();
            text.AppendLine($"{request.Repository}#{issue.Number} [{issue.State}] {OneLine(issue.Title)}");
            text.AppendLine($"  {issue.Url}");
            text.AppendLine($"  Current labels: {Display(issue.CurrentLabels)}");
            text.AppendLine($"  Current areas: {Display(issue.CurrentAreas)}");
            text.AppendLine($"  Prediction: {(issue.Suggestions is null ? "FAILED" : issue.Suggestions.Length == 0 ? "(abstained)" : string.Join(", ", issue.Suggestions.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.LabelName} ({s.Confidence:P1})"))))}");

            if (issue.Suggestions is not null)
            {
                text.AppendLine($"  Current comparison: {(issue.CurrentAreas.Length == 0 ? "UNSCORED (no current area labels)" : AreaLabelHistory.Equal(issue.CurrentAreas, issue.Predicted) ? "MATCH" : "DIFFERENT")}");
                text.AppendLine($"  Missing vs current: {Display(issue.CurrentAreas.Except(issue.Predicted, StringComparer.OrdinalIgnoreCase))}");
                text.AppendLine($"  Extra vs current: {Display(issue.Predicted.Except(issue.CurrentAreas, StringComparer.OrdinalIgnoreCase))}");
            }

            text.AppendLine($"  Original outcome: {issue.History?.Status ?? "Timeline unavailable"}");

            if (issue.History is { } history)
            {
                text.AppendLine($"  Original labels: {(history.ObservedLabeler ? Display(history.OriginalLabels) : "(not observed; not an abstention)")}");

                if (issue.Suggestions is not null && history is { ObservedLabeler: true, Consistent: true })
                {
                    string[] original = AreaLabelHistory.Areas(history.OriginalLabels);
                    text.AppendLine($"  Original comparison: {(AreaLabelHistory.Equal(original, issue.Predicted) ? "MATCH" : "DIFFERENT")}");
                    text.AppendLine($"  Missing vs original: {Display(original.Except(issue.Predicted, StringComparer.OrdinalIgnoreCase))}");
                    text.AppendLine($"  Extra vs original: {Display(issue.Predicted.Except(original, StringComparer.OrdinalIgnoreCase))}");
                }

                foreach (AreaLabelEvent e in history.Events)
                {
                    text.AppendLine($"    {e.At:O} {OneLine(e.Actor)} {(e.Added ? "+" : "-")}{OneLine(e.Label)}");
                }
            }

            foreach (string error in issue.Errors)
            {
                text.AppendLine($"  ERROR: {OneLine(error)}");
            }
        }

        return text.ToString();
    }

    private static void AppendComparisons(StringBuilder text, string title, IEnumerable<(string[] Reference, string[] Actual)> comparisons)
    {
        var all = comparisons.ToArray();
        var differences = all.Where(c => !AreaLabelHistory.Equal(c.Reference, c.Actual)).ToArray();
        text.AppendLine();
        text.AppendLine($"{title}: {all.Length - differences.Length}/{all.Length} exact matches; {differences.Length} differences");
        text.AppendLine("  Mismatched label combinations (reference => actual):");

        foreach (var group in differences.GroupBy(c => $"{Display(c.Reference)} => {Display(c.Actual)}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"    {group.Count()}: {group.Key}");
        }

        text.AppendLine("  Missing reference labels and their unexpected replacement combinations:");

        var misses = differences.SelectMany(c => c.Reference.Except(c.Actual, StringComparer.OrdinalIgnoreCase)
            .Select(label => (Label: label, Replacements: Display(c.Actual.Except(c.Reference, StringComparer.OrdinalIgnoreCase)))));

        foreach (var group in misses.GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"    {group.Key}: missed {group.Count()} of {all.Count(c => c.Reference.Contains(group.Key, StringComparer.OrdinalIgnoreCase))} reference issues");

            foreach (var replacement in group.GroupBy(m => m.Replacements, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine($"      {replacement.Count()} => {replacement.Key}");
            }
        }

        text.AppendLine("  Unexpected labels (issue counts):");

        foreach (var group in differences.SelectMany(c => c.Actual.Except(c.Reference, StringComparer.OrdinalIgnoreCase))
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"    {group.Key}: {group.Count()}");
        }
    }

    private static string Display(IEnumerable<string> labels)
    {
        string[] values = AreaLabelHistory.Normalize(labels);

        return values.Length == 0 ? "(none)" : string.Join(", ", values.Select(l => $"\"{OneLine(l).Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private static string OneLine(string text) => text?.ReplaceLineEndings(" ") ?? "";
}
