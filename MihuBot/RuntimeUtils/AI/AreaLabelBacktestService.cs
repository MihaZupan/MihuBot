using System.Globalization;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;

namespace MihuBot.RuntimeUtils.AI;

internal sealed record AreaLabelBacktestRequest(string Repository, int? IssueNumber, int Count, string LabelerActor);

public sealed class AreaLabelBacktestService
{
    internal const int MaxConcurrentPredictions = 8;

    private readonly GitHubClient _github;
    private readonly GithubGraphQLClient _graphQL;
    private readonly Func<string, CancellationToken, Task<RepositoryInfo>> _getRepository;
    private readonly Func<RepositoryInfo, IssueInfo, AreaLabelPredictionSettings, CancellationToken, Task<AreaLabelSuggestion[]>> _predict;
    private readonly Func<AreaLabelPredictionSettings> _getPredictionSettings;
    private readonly Action<string> _debugLog;

    public AreaLabelBacktestService(GitHubClient github, GithubGraphQLClient graphQL, GitHubDataIngestionService ingestion, AreaLabelDetector detector, Logger logger)
        : this(github, graphQL,
            (repo, ct) => ingestion.TryGetRepositoryInfoAsync(
                ingestion.Stats.TrackedRepos.FirstOrDefault(r => r.RepoName.Equals(repo, StringComparison.OrdinalIgnoreCase))?.RepoName ?? repo, ct),
            (repo, issue, settings, ct) => detector.GetSuggestionsAsync(repo, issue, settings, cancellationToken: ct),
            message => logger.DebugLog(message),
            detector.GetPredictionSettings)
    {
    }

    internal AreaLabelBacktestService(
        GitHubClient github,
        GithubGraphQLClient graphQL,
        Func<string, CancellationToken, Task<RepositoryInfo>> getRepository,
        Func<RepositoryInfo, IssueInfo, AreaLabelPredictionSettings, CancellationToken, Task<AreaLabelSuggestion[]>> predict,
        Action<string> debugLog,
        Func<AreaLabelPredictionSettings> getPredictionSettings)
    {
        _github = github;
        _graphQL = graphQL;
        _getRepository = getRepository;
        _predict = predict;
        _debugLog = debugLog;
        _getPredictionSettings = getPredictionSettings;
    }

    internal async Task<AreaLabelBacktestReport> RunAsync(
        AreaLabelBacktestRequest request, CancellationToken cancellationToken, Func<int, int, Task> progress = null)
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

        var settings = _getPredictionSettings();
        var report = new AreaLabelBacktestReport(request)
        {
            Model = settings.Model,
            ReasoningEffort = settings.ReasoningEffort.ToString(),
        };
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

        object logLock = new();
        using var progressLock = new SemaphoreSlim(1, 1);
        int completed = 0;

        if (progress is not null)
        {
            await progress(0, issues.Count);
        }

        try
        {
            foreach (Issue[] batch in issues.Chunk(GitHubGraphQL.LabelTimelineBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (timelines, _, _, _) = await _graphQL.GetIssueLabelTimelinesAsync([.. batch.Select(i => i.NodeId)], _debugLog, cancellationToken);

                var evaluations = new AreaLabelEvaluation[batch.Length];
                int nextIndex = -1;

                await Task.WhenAll(Enumerable.Range(0, Math.Min(MaxConcurrentPredictions, batch.Length)).Select(_ => EvaluateIssuesAsync()));

                report.Issues.AddRange(evaluations.Where(e => e is not null));
                cancellationToken.ThrowIfCancellationRequested();

                async Task EvaluateIssuesAsync()
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int i = Interlocked.Increment(ref nextIndex);

                        if (i >= batch.Length)
                        {
                            break;
                        }

                        Issue issue = batch[i];

                        var evaluation = new AreaLabelEvaluation(
                            issue.Number, issue.HtmlUrl, issue.Title, issue.State.ToString(),
                            AreaLabelHistory.Normalize(issue.Labels.Select(l => l.Name)));

                        evaluations[i] = evaluation;

                        if (timelines[i].Error is { } error)
                        {
                            Log($"Failed to fetch label timeline for {issue.HtmlUrl}: {error}");
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
                            evaluation.Suggestions = await _predict(repo, CreatePredictionInput(repo, issue), settings, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            evaluation.Errors.Add("Cancelled before evaluation completed.");

                            break;
                        }
                        catch (Exception ex)
                        {
                            Log($"Failed to predict labels for {issue.HtmlUrl}: {ex}");
                            evaluation.Errors.Add($"Prediction failed: {ex.Message}");
                        }

                        if (progress is not null)
                        {
                            await progressLock.WaitAsync();

                            try
                            {
                                await progress(++completed, issues.Count);
                            }
                            finally
                            {
                                progressLock.Release();
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report.Cancelled = true;
        }

        return report;

        void Log(string message)
        {
            lock (logLock)
            {
                _debugLog(message);
            }
        }
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
    public string[] Predicted => [.. Suggestions.Take(1).Select(s => s.LabelName)];
    public string[] AllPredicted => AreaLabelHistory.Normalize(Suggestions.Select(s => s.LabelName));
    public AreaLabelHistory History { get; set; }
    public List<string> Errors { get; } = [];
}

internal sealed record AreaLabelEvent(DateTimeOffset At, string Actor, bool IsHuman, bool Added, string Label, bool IsBot = false)
{
    internal bool IsLabeler(string labelerActor) =>
        Actor.Equals(labelerActor, StringComparison.OrdinalIgnoreCase) ||
        (IsBot && NormalizeBotLogin(Actor).Equals(NormalizeBotLogin(labelerActor), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeBotLogin(string login) =>
        login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ? login[..^5] : login;

    internal static AreaLabelEvent FromGraphQL(GitHubGraphQL.LabelTimelineEvent item, string labelerActor)
    {
        var actor = item.Actor;

        bool human = actor is not null && new UserInfo
        {
            Id = actor.DatabaseId ?? 0,
            Login = actor.Login,
            Type = actor.Type == "User" ? AccountType.User : actor.Type == "Bot" ? AccountType.Bot : null,
        }.IsLikelyARealUser() && !actor.Login.Equals(labelerActor, StringComparison.OrdinalIgnoreCase);

        return new(item.CreatedAt, actor?.Login ?? "(unknown)", human, item.Type == "LabeledEvent", item.Label.Name, actor?.Type == "Bot");
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
                e.Event.Value == EventInfoState.Labeled, e.Label.Name, e.Actor?.Type == AccountType.Bot))
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

        bool IsLabeler(AreaLabelEvent e) => e.IsLabeler(labelerActor);
    }
}

internal sealed class AreaLabelBacktestReport(AreaLabelBacktestRequest request)
{
    public string Model { get; init; }
    public string ReasoningEffort { get; init; }
    public List<AreaLabelEvaluation> Issues { get; } = [];
    public bool Cancelled { get; set; }
    internal AreaLabelEvaluation[] PredictedIssues => [.. Issues.Where(i => i.Suggestions is not null)];
    internal AreaLabelEvaluation[] CurrentScored => [.. PredictedIssues.Where(i => i.CurrentAreas.Length > 0)];
    internal AreaLabelEvaluation[] OriginalScored => [.. PredictedIssues.Where(i => i.History is { ObservedLabeler: true, Consistent: true })];

    public string Summary =>
        $"{(Cancelled ? "Label evaluation CANCELLED (partial report)" : "Label evaluation")}: {Issues.Count}/{request.Count} issues evaluated, {PredictedIssues.Length} predicted, " +
        $"{Issues.Count(i => i.Errors.Count > 0)} with errors. " +
        $"Top-answer exact matches: current area labels {CurrentScored.Count(i => AreaLabelHistory.Equal(i.CurrentAreas, i.Predicted))}/{CurrentScored.Length}; " +
        $"original labeler {OriginalScored.Count(i => AreaLabelHistory.Equal(AreaLabelHistory.Areas(i.History.OriginalLabels), i.Predicted))}/{OriginalScored.Length}. " +
        "No GitHub labels changed.";

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"Area label evaluation: {request.Repository}");
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC"));
        text.AppendLine(Summary);
        text.AppendLine($"Prediction model: {Model}; reasoning effort: {ReasoningEffort}");
        text.AppendLine($"Original labeler actor: {request.LabelerActor}");
        text.AppendLine(request.IssueNumber is { } number ? $"Scope: issue #{number}." : "Scope: newest issues, open and closed; no pull requests.");
        text.AppendLine("Scoring: top answer vs full area-label set; no answer / needs-area-label = abstention. Lower suggestions do not change matches.");
        text.AppendLine("Caveats: current title/body and today's index, not a historical replay. Reference labels are not ground truth.");
        text.AppendLine("Original = first actor application; workflow attribution unverified. No event does not prove a skip; human edits need not be corrections.");
        text.AppendLine("Unscored: prediction errors; no current areas (current comparison); unknown/inconsistent history (original comparison).");
        text.AppendLine();

        text.AppendLine("Original labeler outcomes:");

        foreach (var group in Issues.GroupBy(i => i.History?.Status ?? "Timeline unavailable").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"  {group.Count()}: {group.Key}");
        }

        AppendPredictionComparisons(text, "Current area labels -> prediction", CurrentScored.Select(i => (i.CurrentAreas, i)));
        AppendPredictionComparisons(text, "Original labeler -> prediction", OriginalScored.Select(i => (AreaLabelHistory.Areas(i.History.OriginalLabels), i)));

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
            text.Append($"  Current areas: {Display(issue.CurrentAreas)} | Prediction: {(issue.Suggestions is null ? "FAILED" : issue.Suggestions.Length == 0 ? "(abstained)" : FormatSuggestion(issue.Suggestions[0]))}");

            if (issue.Suggestions is not null)
            {
                text.Append($" | Current comparison: {(issue.CurrentAreas.Length == 0 ? "UNSCORED (no current area labels)" : AreaLabelHistory.Equal(issue.CurrentAreas, issue.Predicted) ? "MATCH" : "DIFFERENT")}");
            }

            text.AppendLine();

            if (issue.Suggestions is { Length: > 1 })
            {
                text.AppendLine($"  Lower-ranked suggestions: {string.Join(", ", issue.Suggestions.Skip(1).Select(FormatSuggestion))}");
            }

            if (issue.Suggestions is not null)
            {
                if (issue.CurrentAreas.Length > 0)
                {
                    AppendLabelDifferences(text, "current", issue.CurrentAreas, issue.Predicted);
                }

                AppendLowerRankedReferences(text, "current", issue.CurrentAreas, issue);
            }

            text.AppendLine($"  Original outcome: {issue.History?.Status ?? "Timeline unavailable"}");

            if (issue.History is { } history)
            {
                text.AppendLine($"  Original labels: {(history.ObservedLabeler ? Display(history.OriginalLabels) : "(not observed; not an abstention)")}");

                if (issue.Suggestions is not null && history is { ObservedLabeler: true, Consistent: true })
                {
                    string[] original = AreaLabelHistory.Areas(history.OriginalLabels);
                    text.AppendLine($"  Original comparison: {(AreaLabelHistory.Equal(original, issue.Predicted) ? "MATCH" : "DIFFERENT")}");
                    AppendLabelDifferences(text, "original", original, issue.Predicted);
                    AppendLowerRankedReferences(text, "original", original, issue);
                }

                if (!history.Consistent || history.HumanChanged || !history.ObservedLabeler ||
                    !AreaLabelHistory.Equal(AreaLabelHistory.Areas(history.OriginalLabels), issue.CurrentAreas))
                {
                    foreach (AreaLabelEvent e in history.Events)
                    {
                        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"    {e.At.UtcDateTime:yyyy-MM-dd HH:mm}Z {OneLine(e.Actor)} {(e.Added ? "+" : "-")}{OneLine(e.Label)}"));
                    }
                }
            }

            foreach (string error in issue.Errors)
            {
                text.AppendLine($"  ERROR: {OneLine(error)}");
            }
        }

        return text.ToString();
    }

    private static string FormatSuggestion(AreaLabelSuggestion suggestion) =>
        string.Create(CultureInfo.InvariantCulture, $"{suggestion.LabelName} ({suggestion.Confidence:P1})");

    private static void AppendLabelDifferences(StringBuilder text, string referenceName, string[] reference, string[] predicted)
    {
        string[] missing = [.. reference.Except(predicted, StringComparer.OrdinalIgnoreCase)];
        string[] extra = [.. predicted.Except(reference, StringComparer.OrdinalIgnoreCase)];

        if (missing.Length > 0)
        {
            text.AppendLine($"  Missing vs {referenceName}: {Display(missing)}");
        }

        if (extra.Length > 0)
        {
            text.AppendLine($"  Extra vs {referenceName}: {Display(extra)}");
        }
    }

    private static void AppendLowerRankedReferences(StringBuilder text, string referenceName, string[] reference, AreaLabelEvaluation issue)
    {
        if (reference.Length == 0 || AreaLabelHistory.Equal(reference, issue.Predicted))
        {
            return;
        }

        string[] included = issue.Suggestions.Select((s, index) => (Suggestion: s, Rank: index + 1))
            .Where(s => s.Rank > 1 && reference.Contains(s.Suggestion.LabelName, StringComparer.OrdinalIgnoreCase))
            .Select(s => $"{OneLine(s.Suggestion.LabelName)} (rank {s.Rank})")
            .ToArray();

        if (included.Length > 0)
        {
            text.AppendLine($"  Lower-ranked reference labels vs {referenceName}: {string.Join(", ", included)}");
        }
    }

    private static void AppendPredictionComparisons(StringBuilder text, string title, IEnumerable<(string[] Reference, AreaLabelEvaluation Issue)> comparisons)
    {
        var all = comparisons.ToArray();
        AppendComparisons(text, title, all.Select(c => (c.Reference, c.Issue.Predicted)));

        var differences = all.Where(c => c.Reference.Length > 0 && !AreaLabelHistory.Equal(c.Reference, c.Issue.Predicted)).ToArray();
        if (differences.Length > 0)
        {
            int included = differences.Count(c => c.Reference.All(label => c.Issue.AllPredicted.Contains(label, StringComparer.OrdinalIgnoreCase)));
            text.AppendLine($"  All reference labels in suggestions: {included}/{differences.Length} mismatches (excluding abstention references).");
        }

        AppendConfidenceBreakdown(text, all);
    }

    private static void AppendConfidenceBreakdown(StringBuilder text, (string[] Reference, AreaLabelEvaluation Issue)[] comparisons)
    {
        if (comparisons.Length == 0)
        {
            return;
        }

        var buckets = comparisons.ToLookup(c => c.Issue.Suggestions.Length == 0 ? 7 : c.Issue.Suggestions[0].Confidence switch
        {
            >= 0.5 and < 0.6 => 0,
            >= 0.6 and < 0.7 => 1,
            >= 0.7 and < 0.8 => 2,
            >= 0.8 and < 0.9 => 3,
            >= 0.9 and < 0.95 => 4,
            >= 0.95 and <= 1 => 5,
            _ => 6,
        });

        string[] names = ["0.5-0.6", "0.6-0.7", "0.7-0.8", "0.8-0.9", "0.9-0.95", "0.95+", "Missing/invalid confidence", "Abstained (no confidence)"];
        text.AppendLine("  Accuracy by top-answer confidence:");
        text.AppendLine("    Band: matches/total (accuracy); [low, high), 0.95+ includes 1.0");

        for (int i = 0; i < names.Length; i++)
        {
            var bucket = buckets[i].ToArray();

            if (i >= 6 && bucket.Length == 0)
            {
                continue;
            }

            int matches = bucket.Count(c => AreaLabelHistory.Equal(c.Reference, c.Issue.Predicted));
            string accuracy = bucket.Length == 0 ? "N/A" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * matches / bucket.Length:F1}%");
            text.AppendLine($"    {names[i]}: {matches}/{bucket.Length} ({accuracy})");
        }
    }

    private static void AppendComparisons(StringBuilder text, string title, IEnumerable<(string[] Reference, string[] Actual)> comparisons)
    {
        var all = comparisons.ToArray();
        var differences = all.Where(c => !AreaLabelHistory.Equal(c.Reference, c.Actual)).ToArray();
        text.AppendLine();
        text.AppendLine($"{title}: {all.Length - differences.Length}/{all.Length} exact matches; {differences.Length} differences");

        if (differences.Length == 0)
        {
            return;
        }

        text.AppendLine("  Mismatches (reference => actual):");

        foreach (var group in differences.GroupBy(c => $"{Display(c.Reference)} => {Display(c.Actual)}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"    {group.Count()}: {group.Key}");
        }

        var misses = differences.SelectMany(c => c.Reference.Except(c.Actual, StringComparer.OrdinalIgnoreCase)
            .Select(label => (Label: label, Replacements: Display(c.Actual.Except(c.Reference, StringComparer.OrdinalIgnoreCase))))).ToArray();

        if (misses.Length > 0)
        {
            text.AppendLine("  Missed labels and replacements:");
        }

        foreach (var group in misses.GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"    {group.Key}: missed {group.Count()} of {all.Count(c => c.Reference.Contains(group.Key, StringComparer.OrdinalIgnoreCase))} reference issues");

            foreach (var replacement in group.GroupBy(m => m.Replacements, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine($"      {replacement.Count()} => {replacement.Key}");
            }
        }

        var unexpected = differences.SelectMany(c => c.Actual.Except(c.Reference, StringComparer.OrdinalIgnoreCase))
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();

        if (unexpected.Length > 0)
        {
            text.AppendLine("  Unexpected labels (issues):");
        }

        foreach (var group in unexpected)
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
