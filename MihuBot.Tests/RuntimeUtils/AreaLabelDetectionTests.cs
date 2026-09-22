using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using MihuBot.Helpers.AI;
using MihuBot.RuntimeUtils.AI;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using MihuBot.RuntimeUtils.Search;
using MihuBot.Tests.Configuration;
using Octokit;
using Octokit.Internal;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelDetectionTests
{
    [Fact]
    public void AutomaticLabelPredictionPauseIsStoredIndependentlyOfOtherServices()
    {
        var configuration = new TestConfigurationService();
        var services = new ServiceConfiguration(configuration);

        Assert.False(services.PauseAutoLabelPrediction);

        services.PauseAutoLabelPrediction = true;

        Assert.True(new ServiceConfiguration(configuration).PauseAutoLabelPrediction);
        Assert.False(services.PauseGitHubPolling);
        Assert.False(services.PauseAutoTriage);
        Assert.False(services.PauseAutoDuplicateDetection);
        Assert.True(configuration.TryGet(null, nameof(ServiceConfiguration.PauseAutoLabelPrediction), out string stored));
        Assert.Equal(bool.TrueString, stored);

        services.PauseAutoLabelPrediction = false;

        Assert.False(new ServiceConfiguration(configuration).PauseAutoLabelPrediction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptCaptureMatchesTheExactTextPassedToTheChatClient(bool capture)
    {
        string prompt = "instructions\r\n```json\n" + new string('x', 50_000) + "\n```\n\u00e9\u4e2d";
        string? captured = null;
        int captures = 0;
        int requests = 0;
        using var cancellation = new CancellationTokenSource();
        using var chat = new TestChatClient((messages, options, ct) =>
        {
            requests++;
            Assert.Equal(prompt, Assert.Single(messages).Text);
            Assert.Equal(cancellation.Token, ct);
            Assert.NotNull(options?.ResponseFormat);
            Assert.Equal(capture ? prompt : null, captured);
            return Task.FromException<ChatResponse>(new InvalidOperationException("Model unavailable"));
        });
        Action<string>? onPrompt = capture ? text =>
        {
            captures++;
            captured = text;
        } : null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AreaLabelDetector.GetPredictionResponseAsync(chat, prompt, new(), cancellation.Token, onPrompt!));

        Assert.Equal(1, requests);
        Assert.Equal(capture ? 1 : 0, captures);
        Assert.Equal(capture ? prompt : null, captured);
    }

    [Fact]
    public async Task CancelledSubmissionDoesNotCaptureAnUnusedPrompt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var chat = new TestChatClient((_, _, _) => throw new InvalidOperationException("Must not submit a request."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AreaLabelDetector.GetPredictionResponseAsync(chat, "unused", new(), cancellation.Token, _ => Assert.Fail("Must not capture an unused prompt.")));
    }

    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            respond(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    internal static string GetPredictionCacheKey(
        string repository = "dotnet/runtime", int number = 123, string prefix = "area-",
        string model = OpenAIService.DefaultModel, string effort = "high",
        IssueType type = IssueType.Issue, DateTime updatedAt = default) =>
        AreaLabelDetector.GetCacheKey(new IssueInfo
        {
            Repository = new() { FullName = repository }, Number = number, IssueType = type, UpdatedAt = updatedAt,
        }, prefix, new(model, new(effort)));

    internal static AreaLabelDetector CreateCachedDetector(HybridCache cache, TestConfigurationService? configuration = null, IssueType type = IssueType.Issue) =>
        new((repository, number, _) => Task.FromResult(new IssueInfo
        {
            Repository = new() { FullName = repository },
            Number = number,
            IssueType = type,
        }), cache, configuration ?? new TestConfigurationService());

    [Fact]
    public void PredictionCacheKeysAreBoundedHashesWithNormalizedRepositoryAndPrefix()
    {
        string key = GetPredictionCacheKey();

        Assert.Matches(@"^AreaLabelDetector/PredictAsync/[A-Za-z0-9_-]{86}$", key);
        Assert.Equal(key, GetPredictionCacheKey(repository: "DOTNET/RUNTIME", prefix: "AREA-"));
        Assert.Equal(key.Length, GetPredictionCacheKey(model: new string('m', 2000), prefix: new string('p', 100)).Length);
        Assert.NotEqual(key, GetPredictionCacheKey(repository: "dotnet/other"));
        Assert.NotEqual(key, GetPredictionCacheKey(type: IssueType.Discussion));
        Assert.NotEqual(GetPredictionCacheKey(model: "a:b", effort: "c"), GetPredictionCacheKey(model: "a", effort: "b:c"));
    }

    [Fact]
    public async Task PullRequestSimilarityIncludesHighScoringLabeledIssuesPullRequestsAndDiscussions()
    {
        var response = CreateSimilarIssueSearchResponse("candidate", 0.9, 4);
        response.Results[0].Issue.IssueType = IssueType.Issue;
        response.Results[1].Issue.IssueType = IssueType.PullRequest;
        response.Results[2].Issue.IssueType = IssueType.Discussion;
        response.Results[3].Issue.IssueType = IssueType.Discussion;
        response.Results[3].Issue.Labels = [];
        var results = await AreaLabelDetector.FindSimilarIssuesAsync(
            new IssueInfo { Id = "target", IssueType = IssueType.PullRequest }, ["area-Test"],
            (_, _) => Task.FromResult(response), [], CancellationToken.None);

        Assert.Equal(["candidate-0", "candidate-1", "candidate-2"], results.Select(i => i.Id));
    }

    [Theory]
    [InlineData(IssueType.Issue, 120)]
    [InlineData(IssueType.Discussion, 120)]
    [InlineData(IssueType.PullRequest, 2)]
    public void PullRequestsHaveShortCacheLifetimes(IssueType type, int minutes)
    {
        var options = AreaLabelDetector.GetCacheOptions(type);

        Assert.Equal(TimeSpan.FromMinutes(minutes), options.Expiration);

        if (type == IssueType.PullRequest)
        {
            Assert.Equal(options.Expiration, options.LocalCacheExpiration);
        }
    }

    [Fact]
    public async Task PullRequestCacheSeparatesOldIssuePredictionsAndUpdatedDrafts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        AreaLabelSuggestion[] old = [new("area-Old", 1)];
        AreaLabelSuggestion[] initial = [];
        AreaLabelSuggestion[] updated = [new("area-Current", 0.9)];
        await cache.SetAsync(GetPredictionCacheKey(), old);
        await cache.SetAsync(GetPredictionCacheKey(type: IssueType.PullRequest), initial);
        await cache.SetAsync(GetPredictionCacheKey(type: IssueType.PullRequest, updatedAt: new DateTime(1, DateTimeKind.Utc)), updated);
        using var detector = CreateCachedDetector(cache, type: IssueType.PullRequest);
        var issue = new IssueInfo
        {
            Repository = new() { FullName = "dotnet/runtime" }, Number = 123, IssueType = IssueType.PullRequest,
        };

        Assert.Equal(initial, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        issue.UpdatedAt = new DateTime(1, DateTimeKind.Utc);
        Assert.Equal(updated, await detector.PredictAsync(issue, "area-", CancellationToken.None));
    }

    [Theory]
    [InlineData(IssueType.Issue)]
    [InlineData(IssueType.PullRequest)]
    public void AutomaticDetectionDoesNotRevisitUpdatedItems(IssueType type)
    {
        var issue = new IssueInfo { HtmlUrl = "https://github.com/dotnet/runtime/pull/123", IssueType = type };
        string original = DetectIssueAreaLabelsService.GetProcessedKey(issue);
        issue.UpdatedAt = DateTime.UtcNow;
        string updated = DetectIssueAreaLabelsService.GetProcessedKey(issue);

        Assert.Equal(issue.HtmlUrl, original);
        Assert.Equal(original, updated);
    }

    [Theory]
    [InlineData("https://github.com/dotnet/runtime/pull/123#updated-639000000000000000", "https://github.com/dotnet/runtime/pull/123")]
    [InlineData("https://github.com/dotnet/runtime/pull/123#updated-0", "https://github.com/dotnet/runtime/pull/123")]
    [InlineData("https://github.com/dotnet/runtime/pull/123", "https://github.com/dotnet/runtime/pull/123")]
    [InlineData("https://github.com/dotnet/runtime/issues/123", "https://github.com/dotnet/runtime/issues/123")]
    public void PreviouslyProcessedKeysAreNormalizedToItemUrls(string storedKey, string expected)
    {
        Assert.Equal(expected, DetectIssueAreaLabelsService.NormalizeProcessedKey(storedKey));
    }

    [Theory]
    [InlineData(IssueType.Issue, ItemState.Open, null)]
    [InlineData(IssueType.Issue, ItemState.Open, "needs-area-label")]
    [InlineData(IssueType.Issue, ItemState.Closed, "area-VM")]
    [InlineData(IssueType.PullRequest, ItemState.Open, "area-VM")]
    [InlineData(IssueType.PullRequest, ItemState.Closed, null)]
    public void IncomingDetectionIncludesRuntimeItemsRegardlessOfLabelsOrState(IssueType type, ItemState state, string? label)
    {
        DateTime since = DateTime.UtcNow.AddDays(-1);
        var item = new IssueInfo
        {
            RepositoryId = 210716005, IssueType = type, State = state, CreatedAt = since,
            Labels = label is null ? [] : [new() { Name = label }],
        };

        Assert.Same(item, Assert.Single(DetectIssueAreaLabelsService.GetIncomingItems(new[] { item }.AsQueryable(), since)));
    }

    [Fact]
    public void IncomingDetectionExcludesStaleItemsDiscussionsAndOtherRepositories()
    {
        DateTime now = DateTime.UtcNow;
        DateTime since = now.AddDays(-1);
        IssueInfo[] items =
        [
            new() { RepositoryId = 210716005, CreatedAt = since.AddTicks(-1), UpdatedAt = since.AddTicks(-1), IssueType = IssueType.Issue, Labels = [] },
            new() { RepositoryId = 210716005, CreatedAt = now, IssueType = IssueType.Discussion },
            new() { RepositoryId = 1, CreatedAt = now, IssueType = IssueType.PullRequest },
        ];

        Assert.Empty(DetectIssueAreaLabelsService.GetIncomingItems(items.AsQueryable(), since));
    }

    [Theory]
    [InlineData(IssueType.Issue, null)]
    [InlineData(IssueType.Issue, "area-VM")]
    [InlineData(IssueType.PullRequest, null)]
    [InlineData(IssueType.PullRequest, "area-VM")]
    public void RecentlyUpdatedOlderItemsAreExcludedRegardlessOfLabelsOrState(IssueType type, string? label)
    {
        DateTime now = DateTime.UtcNow;
        var item = new IssueInfo
        {
            RepositoryId = 210716005, CreatedAt = now.AddMonths(-1), UpdatedAt = now,
            IssueType = type, State = ItemState.Open, Labels = label is null ? [] : [new() { Name = label }],
        };

        Assert.Empty(DetectIssueAreaLabelsService.GetIncomingItems(new[] { item }.AsQueryable(), now.AddDays(-1)));

        item.State = ItemState.Closed;
        Assert.Empty(DetectIssueAreaLabelsService.GetIncomingItems(new[] { item }.AsQueryable(), now.AddDays(-1)));
    }

    [Fact]
    public void IncomingDetectionDoesNotStarveItemsBeyondTheFirstHundred()
    {
        DateTime since = DateTime.UtcNow.AddDays(-1);
        var items = Enumerable.Range(1, 150).Reverse().Select(number => new IssueInfo
        {
            Number = number, RepositoryId = 210716005, CreatedAt = since.AddMinutes(number), IssueType = IssueType.Issue,
        }).AsQueryable();

        Assert.Equal(Enumerable.Range(1, 150),
            DetectIssueAreaLabelsService.GetIncomingItems(items, since).Select(i => i.Number));
    }

    [Theory]
    [InlineData(IssueType.Issue, 0)]
    [InlineData(IssueType.Issue, 1)]
    [InlineData(IssueType.Issue, 2)]
    [InlineData(IssueType.PullRequest, 0)]
    [InlineData(IssueType.PullRequest, 1)]
    [InlineData(IssueType.PullRequest, 2)]
    public void IncomingPredictionsIncludeTitleCurrentAreasAndExplicitAbstentions(IssueType type, int areaCount)
    {
        var item = new IssueInfo
        {
            HtmlUrl = $"https://github.com/dotnet/runtime/{(type == IssueType.PullRequest ? "pull" : "issues")}/123",
            IssueType = type,
            Number = 123,
            Title = "Fix runtime allocation",
            Labels = [new() { Name = "needs-area-label" }],
        };

        if (areaCount > 0)
        {
            item.Labels.Add(new() { Name = "area-JIT" });
        }

        if (areaCount > 1)
        {
            item.Labels.Add(new() { Name = "AREA-VM" });
        }

        string currentLabels = areaCount switch
        {
            1 => "`area-JIT`",
            2 => "`area-JIT`, `AREA-VM`",
            _ => "<none>",
        };
        string context =
            $"""
            [`Fix runtime allocation` - {(type == IssueType.PullRequest ? "PR " : "")}#123](<{item.HtmlUrl}>)
            - Current: {currentLabels}
            - Suggested:
            """;

        Assert.Equal($"{context} <none>",
            DetectIssueAreaLabelsService.FormatPrediction(item, []));
        Assert.Equal($"{context} `area-VM` ({0.9:F2})",
            DetectIssueAreaLabelsService.FormatPrediction(item, [new("area-VM", 0.9)]));
        Assert.Equal($"{context} `area-VM` ({0.9:F2}), `area-JIT` ({0.75:F2})",
            DetectIssueAreaLabelsService.FormatPrediction(item, [new("area-VM", 0.9), new("area-JIT", 0.75)]));
    }

    [Theory]
    [InlineData(IssueType.PullRequest, "copilot", true)]
    [InlineData(IssueType.PullRequest, "Copilot[bot]", true)]
    [InlineData(IssueType.PullRequest, "github-COPILOT-agent", true)]
    [InlineData(IssueType.PullRequest, "contributor", false)]
    [InlineData(IssueType.PullRequest, null, false)]
    [InlineData(IssueType.Issue, "Copilot[bot]", false)]
    public void IncomingPredictionsMarkOnlyCopilotAuthoredPullRequests(IssueType type, string? login, bool expectedMarker)
    {
        var item = new IssueInfo
        {
            HtmlUrl = $"https://github.com/dotnet/runtime/{(type == IssueType.PullRequest ? "pull" : "issues")}/123",
            IssueType = type,
            Number = 123,
            Title = "Fix runtime allocation",
            Labels = [],
            User = login is null ? null : new() { Login = login },
        };
        string expectedHeader = $"[`Fix runtime allocation` - {(type == IssueType.PullRequest ? "PR " : "")}#123](<{item.HtmlUrl}>)"
            + (expectedMarker ? " (Copilot PR)" : "");

        Assert.Equal(
            $"""
            {expectedHeader}
            - Current: <none>
            - Suggested: <none>
            """,
            DetectIssueAreaLabelsService.FormatPrediction(item, []));
        Assert.Equal(
            $"""
            {expectedHeader}
            - Current: <none>
            - Suggested: `area-VM` ({0.9:F2})
            """,
            DetectIssueAreaLabelsService.FormatPrediction(item, [new("area-VM", 0.9)]));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public void IncomingPredictionTitlesAreLimitedToOneHundredCharacters(int titleLength)
    {
        var item = new IssueInfo
        {
            HtmlUrl = "https://github.com/dotnet/runtime/pull/123",
            IssueType = IssueType.PullRequest,
            Number = 123,
            Title = new string('t', titleLength),
            Labels = [],
        };
        string expectedTitle = titleLength <= 100 ? new string('t', titleLength) : new string('t', 96) + " ...";

        Assert.Equal(
            $"""
            [`{expectedTitle}` - PR #123](<{item.HtmlUrl}>)
            - Current: <none>
            - Suggested: <none>
            """,
            DetectIssueAreaLabelsService.FormatPrediction(item, []));
    }

    [Theory]
    [InlineData(0.9, 24)]
    [InlineData(0.9, 48)]
    [InlineData(0.95, 24)]
    [InlineData(0.95, 48)]
    [InlineData(0.8, 24)]
    [InlineData(0.8, 48)]
    public async Task SimilarIssuesPreferHigherScoresBeforeMoreRecentDateRanges(double score, int age)
    {
        var recent = CreateSimilarIssueSearchResponse("recent", 0.7, 5);
        var older = CreateSimilarIssueSearchResponse("older", score, 5);
        List<int> searches = [];

        var results = await AreaLabelDetector.FindSimilarIssuesAsync(
            new IssueInfo { Id = "target" }, ["area-Test"],
            (months, _) =>
            {
                searches.Add(months);
                return Task.FromResult(months >= age ? older : recent);
            },
            [], CancellationToken.None);

        Assert.Equal(older.Results.Select(r => r.Issue), results);
        Assert.Equal(score >= 0.9 && age == 24 ? [12, 24] : new[] { 12, 24, 48 }, searches);
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(0.95)]
    [InlineData(1.0)]
    [InlineData(0.8)]
    [InlineData(0.7)]
    public async Task SimilarIssuesUseTheFirstDateRangeWithFiveMatchesAtTheBestThreshold(double score)
    {
        var recent = CreateSimilarIssueSearchResponse("recent", score, 5);
        var older = CreateSimilarIssueSearchResponse("older", score, 6);
        List<int> searches = [];

        var results = await AreaLabelDetector.FindSimilarIssuesAsync(
            new IssueInfo { Id = "target" }, ["area-Test"],
            (months, _) =>
            {
                searches.Add(months);
                return Task.FromResult(months == 12 ? recent : older);
            },
            [], CancellationToken.None);

        Assert.Equal(recent.Results.Select(r => r.Issue), results);
        Assert.Equal(score >= 0.9 ? [12] : new[] { 12, 24, 48 }, searches);
    }

    [Fact]
    public async Task SimilarIssuesFallBackToTheOldestRangeAndExcludeIneligibleMatches()
    {
        var oldest = CreateSimilarIssueSearchResponse("oldest", 0.7, 2);
        var excluded = CreateSimilarIssueSearchResponse("excluded", 0.95, 3);
        excluded.Results[0].Issue.Id = "target";
        excluded.Results[1].Issue.Labels = [];
        excluded.Results[2].Issue.Labels = [new() { Name = "area-Other" }];
        var belowThreshold = CreateSimilarIssueSearchResponse("low", 0.6999, 5);
        oldest = oldest with { Results = [.. excluded.Results, .. oldest.Results, .. belowThreshold.Results] };

        var results = await AreaLabelDetector.FindSimilarIssuesAsync(
            new IssueInfo { Id = "target" }, ["AREA-TEST"],
            (months, _) => Task.FromResult(months == 48 ? oldest : GitHubSearchResponse.Empty),
            [], CancellationToken.None);

        Assert.Equal(["oldest-0", "oldest-1"], results.Select(i => i.Id));
    }

    [Theory]
    [InlineData(0.7)]
    [InlineData(0.8)]
    [InlineData(0.899999)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task PullRequestsRejectMatchesBelowTheHighThreshold(double score)
    {
        var results = await AreaLabelDetector.FindSimilarIssuesAsync(
            new IssueInfo { Id = "target", IssueType = IssueType.PullRequest }, ["area-Test"],
            (_, _) => Task.FromResult(CreateSimilarIssueSearchResponse("low", score, 5)),
            [], CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task PullRequestsPreserveSparseStrongMatchesWithoutFillingWithWeakOnes()
    {
        var strong = CreateSimilarIssueSearchResponse("strong", 0.95, 1);
        var weak = CreateSimilarIssueSearchResponse("weak", 0.89, 10);
        var result = await AreaLabelDetector.FindSimilarIssuesAsync(new() { Id = "target", IssueType = IssueType.PullRequest }, ["area-Test"],
            (age, _) => Task.FromResult(age == 12 ? strong : weak), [], CancellationToken.None);

        Assert.Equal("strong-0", Assert.Single(result).Id);
    }

    [Theory]
    [InlineData(IssueType.Issue, 0.7f)]
    [InlineData(IssueType.Discussion, 0.7f)]
    [InlineData(IssueType.PullRequest, 0.9f)]
    public void SearchScoreFloorAllowsIssueFallbackButKeepsPrMatchesStrict(IssueType type, float expected)
    {
        Assert.Equal(expected, AreaLabelDetector.GetMinimumSearchScore(type));
    }

    [Theory]
    [InlineData(0.699999)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task IssuesNeverFallBackBelowPointSeven(double score)
    {
        var result = await AreaLabelDetector.FindSimilarIssuesAsync(new() { Id = "target" }, ["area-Test"],
            (_, _) => Task.FromResult(CreateSimilarIssueSearchResponse("low", score, 5)), [], CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SimilarIssuesAreRankedDeduplicatedAndCapped()
    {
        var response = CreateSimilarIssueSearchResponse("candidate", 0.9, 30);
        response = response with
        {
            Results = [.. response.Results.Select((r, i) => r with { Score = 0.9 + i / 1000d })]
        };
        response.Results[29].Issue.Id = response.Results[28].Issue.Id;
        response.Results[27].Issue.Repository = new() { Private = true };
        var result = await AreaLabelDetector.FindSimilarIssuesAsync(new() { Id = "target" }, ["area-Test"],
            (_, _) => Task.FromResult(response), [], CancellationToken.None);

        Assert.Equal(AreaLabelDetector.MaxSimilarItems, result.Length);
        Assert.Same(response.Results[29].Issue, result[0]);
        Assert.Equal(result.Length, result.Select(i => i.Id).Distinct().Count());
        Assert.DoesNotContain(response.Results[27].Issue, result);
        Assert.Equal("candidate-26", result[1].Id);
    }

    private static GitHubSearchResponse CreateSimilarIssueSearchResponse(string prefix, double score, int count) => new()
    {
        Results = Enumerable.Range(0, count)
            .Select(i => new IssueResultGroup
            {
                Score = score,
                Results =
                [
                    new()
                    {
                        Score = score,
                        Issue = new IssueInfo { Id = $"{prefix}-{i}", Labels = [new() { Name = "area-Test" }] },
                        Comment = null,
                    }
                ],
            })
            .ToArray(),
        Timings = new(),
    };

    [Theory]
    [InlineData(IssueType.Issue)]
    [InlineData(IssueType.PullRequest)]
    [InlineData(IssueType.Discussion)]
    public async Task SimilarIssuesExcludeMentionsBeforeThresholdSelectionAndResultLimits(IssueType type)
    {
        var recent = CreateSimilarIssueSearchResponse("mentioned", 0.95, 5);
        var older = CreateSimilarIssueSearchResponse("other", 0.95, 15);
        List<int> searches = [];

        foreach (var result in recent.Results.Concat(older.Results))
        {
            result.Issue.HtmlUrl = $"https://github.com/dotnet/runtime/issues/{result.Issue.Id}";
        }

        string[] mentioned = [.. recent.Results.Select(r => r.Issue.HtmlUrl.ToUpperInvariant())];
        var results = await AreaLabelDetector.FindSimilarIssuesAsync(new() { Id = "target", IssueType = type }, ["area-Test"],
            (months, _) =>
            {
                searches.Add(months);
                return Task.FromResult(months == 12 ? recent : older with { Results = [.. recent.Results, .. older.Results] });
            }, mentioned, CancellationToken.None);

        Assert.Equal([12, 24], searches);
        Assert.Equal(AreaLabelDetector.MaxSimilarItems, results.Length);
        Assert.All(results, item => Assert.StartsWith("other-", item.Id, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1001)]
    public void RecentLabelQueryIncludesAtMostTheLatestThousandItemsOfAllTypes(int count)
    {
        DateTime now = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var items = Enumerable.Range(1, count).Select(n => LabelUsageItem(n, now.AddYears(-1))).ToList();
        var otherRepository = LabelUsageItem(count + 1, now);
        otherRepository.RepositoryId++;
        var expired = LabelUsageItem(count + 2, now.AddYears(-3));
        expired.UpdatedAt = now;
        items.AddRange([otherRepository, expired]);

        var labels = AreaLabelDetector.QueryRecentlyUsedLabels(items.AsQueryable(), 123, now).ToArray();

        Assert.Equal(items.Take(count).TakeLast(1000).Select(i => i.Labels.Single().Name).Order(StringComparer.Ordinal),
            labels.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(IssueType.Issue)]
    [InlineData(IssueType.PullRequest)]
    [InlineData(IssueType.Discussion)]
    public void RecentLabelQueryStopsAtTwoYearsEvenWithFewerThanAThousandItems(IssueType type)
    {
        DateTime now = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        DateTime cutoff = now.AddYears(-2);
        var items = Enumerable.Range(1, 2).Select(n => LabelUsageItem(n, cutoff.AddMinutes(n))).ToList();
        var boundary = LabelUsageItem(3, cutoff);
        boundary.IssueType = type;
        items.Add(boundary);
        var expired = LabelUsageItem(4, cutoff.AddTicks(-1));
        expired.UpdatedAt = now;
        items.Add(expired);
        items[1].Labels = items[0].Labels;

        var labels = AreaLabelDetector.QueryRecentlyUsedLabels(items.AsQueryable(), 123, now).ToArray();

        Assert.Equal(2, labels.Length);
        Assert.Contains("area-1", labels);
        Assert.Contains("area-3", labels);
        Assert.DoesNotContain("area-4", labels);
    }

    [Fact]
    public void RecentLabelQueryCountsUnlabeledItemsTowardTheThousandItemLimit()
    {
        DateTime now = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var items = Enumerable.Range(1, 1001).Select(n => LabelUsageItem(n, now.AddMinutes(-1001 + n))).ToArray();

        foreach (var item in items.Skip(1))
        {
            item.Labels = [];
        }

        Assert.Empty(AreaLabelDetector.QueryRecentlyUsedLabels(items.AsQueryable(), 123, now));
    }

    [Fact]
    public void RecentLabelQueryTranslatesToPostgresWithoutLoadingIssueContents()
    {
        using var db = new GitHubDbContext(new DbContextOptionsBuilder<GitHubDbContext>()
            .UseNpgsql("Host=localhost;Database=recent_labels_query_test").Options);
        string sql = AreaLabelDetector.QueryRecentlyUsedLabels(db.Issues.AsNoTracking(), 123, DateTime.UtcNow).ToQueryString();

        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
        Assert.Contains("DISTINCT", sql, StringComparison.Ordinal);
        Assert.Contains("\"RepositoryId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"CreatedAt\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Body\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"IssueType\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("comments", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateLabelUsageCacheIsSharedAcrossPrefixesButNotRepositories()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        await cache.SetAsync("AreaLabelDetector/RecentLabels/123", new[] { "AREA-USED", "area-Legacy", "component:Used" });
        await cache.SetAsync("AreaLabelDetector/RecentLabels/456", Array.Empty<string>());
        using var detector = new AreaLabelDetector(null!, null!, null!, null!, cache, null!, new TestConfigurationService(), null!, null!);
        var repository = new RepositoryInfo
        {
            Id = 123,
            Labels =
            [
                new() { Name = "area-Used" },
                new() { Name = "area-Unused" },
                new() { Name = "area-Legacy", Description = "Deprecated label." },
                new() { Name = "component:Used" },
            ],
        };

        Assert.Equal(["area-Used"], await detector.GetCandidateLabelsAsync(repository, "area-", CancellationToken.None));
        Assert.Equal(["component:Used"], await detector.GetCandidateLabelsAsync(repository, "COMPONENT:", CancellationToken.None));
        repository.Id = 456;
        Assert.Empty(await detector.GetCandidateLabelsAsync(repository, "area-", CancellationToken.None));
        Assert.Empty(await detector.GetSuggestionsAsync(repository, new IssueInfo(), cancellationToken: CancellationToken.None));
    }

    private static IssueInfo LabelUsageItem(int number, DateTime createdAt) => new()
    {
        Id = $"item-{number}", Number = number, RepositoryId = 123, CreatedAt = createdAt,
        IssueType = (IssueType)(number % 3),
        Labels = [new() { Name = $"area-{number}" }],
    };

    [Theory]
    [InlineData("area-", "area-Test")]
    [InlineData("component:", "component:Runtime")]
    [InlineData("COMPONENT:", "component:Runtime")]
    [InlineData("type/", "type/Bug")]
    public void CandidateLabelsUseTheRequestedPrefix(string prefix, string expected)
    {
        var repository = new RepositoryInfo
        {
            Labels =
            [
                new() { Name = "area-Test" },
                new() { Name = "component:Runtime" },
                new() { Name = "COMPONENT:RUNTIME" },
                new() { Name = "type/Bug" },
            ]
        };

        string[] labels = AreaLabelDetector.GetCandidateLabels(repository, prefix);
        Assert.Equal(expected, Assert.Single(labels));
        var suggestions = AreaLabelDetector.FilterSuggestions(
            [new("unrelated", 1), new(expected.ToUpperInvariant(), 0.9)], labels);
        Assert.Equal(new AreaLabelSuggestion(expected, 0.9), Assert.Single(suggestions));
    }

    [Fact]
    public void MissingPrefixHasNoCandidates()
    {
        var repository = new RepositoryInfo { Labels = [new() { Name = "area-Test" }] };
        Assert.Empty(AreaLabelDetector.GetCandidateLabels(repository, "component:"));
    }

    [Theory]
    [InlineData("Only used for closed issues.")]
    [InlineData("DO NOT ASSIGN ACTIVE ISSUES to this label.")]
    [InlineData("Do not assign to active issues.")]
    [InlineData("Do not use; use area-Current instead.")]
    [InlineData("This label is no longer used.")]
    [InlineData("Legacy label retained for historical tracking.")]
    [InlineData("Deprecated label; use area-Current instead.")]
    [InlineData("Obsolete label.")]
    [InlineData("This label is deprecated.")]
    [InlineData("This label is obsolete.")]
    [InlineData("DEPRECATED")]
    [InlineData("Obsolete")]
    [InlineData("LEGACY")]
    [InlineData("  DEPRECATED  ")]
    [InlineData("No longer assigned to new issues.")]
    [InlineData("Deprecated: Cross-cutting issues related to ASP.NET Core as a platform")]
    [InlineData("*DEPRECATED* This label is deprecated in favor of the area-mvc and area-minimal labels")]
    [InlineData("only use for closed issues")]
    [InlineData("only for closed issues")]
    [InlineData("Do not assign active issues to this label")]
    public void LegacyLabelsAreExcludedFromCandidatesAndSuggestions(string description)
    {
        var repository = new RepositoryInfo
        {
            Labels =
            [
                new() { Name = "area-Legacy", Description = description },
                new() { Name = "area-Current", Description = "Current issues." },
            ]
        };

        string[] labels = AreaLabelDetector.GetCandidateLabels(repository, "area-");
        Assert.Equal("area-Current", Assert.Single(labels));
        var suggestions = AreaLabelDetector.FilterSuggestions(
            [new("area-Legacy", 1), new("area-Current", 0.9)], labels);
        Assert.Equal(new AreaLabelSuggestion("area-Current", 0.9), Assert.Single(suggestions));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Runtime and libraries.")]
    [InlineData("Compatibility with legacy systems.")]
    [InlineData("Issues involving deprecated APIs.")]
    [InlineData("Issues involving obsolete APIs.")]
    [InlineData("Feedback about a deprecated NuGet package.")]
    [InlineData("ML.NET command-line entry points and legacy MAML tooling")]
    [InlineData("Can no longer be reproduced on latest")]
    [InlineData("Issues/PRs that are associated with transitioning our legacy UI Tests to Appium")]
    public void ActiveLabelsRemainCandidates(string? description)
    {
        var repository = new RepositoryInfo
        {
            Labels = [new() { Name = "component:Runtime", Description = description }]
        };

        Assert.Equal("component:Runtime", Assert.Single(AreaLabelDetector.GetCandidateLabels(repository, "component:")));
    }

    [Fact]
    public async Task OnlyLegacyLabelsSkipsPrediction()
    {
        var repository = new RepositoryInfo
        {
            Labels = [new() { Name = "area-Legacy", Description = "For closed issues only." }]
        };
        using var detector = new AreaLabelDetector(null!, null!, null!, null!, null!, null!, new TestConfigurationService(), null!, null!);

        Assert.Empty(await detector.GetSuggestionsAsync(repository, new IssueInfo(), cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task PredictionCacheSeparatesPrefixesAndItems()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        AreaLabelSuggestion[] area = [new("area-Test", 0.9)];
        AreaLabelSuggestion[] component = [new("component:Runtime", 0.9)];
        AreaLabelSuggestion[] discussion = [new("area-Discussion", 0.9)];
        await cache.SetAsync(GetPredictionCacheKey(), area);
        await cache.SetAsync(GetPredictionCacheKey(prefix: "component:"), component);
        await cache.SetAsync(GetPredictionCacheKey(number: 124), discussion);
        using var detector = CreateCachedDetector(cache);

        Assert.Equal(area, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));
        Assert.Equal(component, await detector.PredictAsync("DOTNET/RUNTIME", 123, "COMPONENT:", CancellationToken.None));
        Assert.Equal(discussion, await detector.PredictAsync("dotnet/runtime", 124, "area-", CancellationToken.None));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task LegacyGitHubToolsSettingDoesNotSelectToolBasedCachedPredictions(string enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        var configuration = new TestConfigurationService();
        configuration.Set(null, "AreaLabelDetector.GitHubTools", enabled);
        AreaLabelSuggestion[] original = [new("area-Original", 0.9)];
        AreaLabelSuggestion[] tools = [new("area-Tools", 0.8)];
        string key = GetPredictionCacheKey();
        await cache.SetAsync(key, original);
        await cache.SetAsync($"{key}:github-mcp-v2", tools);
        using var detector = CreateCachedDetector(cache, configuration);

        Assert.Equal(original, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));
    }

    [Theory]
    [InlineData("test-model")]
    [InlineData("provider/model:version")]
    public async Task ModelChangesSelectSeparateCachedPredictionsImmediately(string model)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        var configuration = new TestConfigurationService();
        AreaLabelSuggestion[] original = [new("area-Original", 0.9)];
        AreaLabelSuggestion[] alternative = [new("area-Alternative", 0.8)];
        await cache.SetAsync(GetPredictionCacheKey(), original);
        await cache.SetAsync(GetPredictionCacheKey(model: model), alternative);
        using var detector = CreateCachedDetector(cache, configuration);

        Assert.Equal(original, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        configuration.Set(null, "AreaLabelDetector.Model", model);
        Assert.Equal(alternative, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        configuration.Remove(null, "AreaLabelDetector.Model");
        Assert.Equal(original, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void ReasoningConfigurationIsAppliedToChatOptions(string effort)
    {
        var configuration = new TestConfigurationService();
        using var detector = new AreaLabelDetector(null!, null!, null!, null!, null!, null!, configuration, null!, null!);

#pragma warning disable OPENAI001
        Assert.Equal("high", detector.CreateChatCompletionOptions().ReasoningEffortLevel.ToString());

        configuration.Set(null, "AreaLabelDetector.ReasoningEffort", effort);
        Assert.Equal(effort, detector.CreateChatCompletionOptions().ReasoningEffortLevel.ToString());

        var settings = detector.GetPredictionSettings();
        var oneShotOptions = AreaLabelDetector.CreateChatOptions(settings);
        Assert.Null(oneShotOptions.Tools);
        Assert.Equal(AreaLabelDetector.MaxOutputTokens, oneShotOptions.MaxOutputTokens);
        var completionOptions = Assert.IsType<OpenAI.Chat.ChatCompletionOptions>(oneShotOptions.RawRepresentationFactory!(null!));
        Assert.Equal(effort, completionOptions.ReasoningEffortLevel.ToString());
        Assert.Equal(AreaLabelDetector.MaxOutputTokens, completionOptions.MaxOutputTokenCount);
        Assert.Empty(completionOptions.Tools);
        Assert.NotSame(completionOptions, oneShotOptions.RawRepresentationFactory!(null!));

        configuration.Remove(null, "AreaLabelDetector.ReasoningEffort");
        Assert.Equal("high", detector.CreateChatCompletionOptions().ReasoningEffortLevel.ToString());
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData("none")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task ReasoningChangesSelectSeparateCachedPredictionsImmediately(string effort)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        var configuration = new TestConfigurationService();
        AreaLabelSuggestion[] original = [new("area-Original", 0.9)];
        AreaLabelSuggestion[] alternative = [new("area-Alternative", 0.8)];
        await cache.SetAsync(GetPredictionCacheKey(), original);
        await cache.SetAsync(GetPredictionCacheKey(effort: effort), alternative);
        using var detector = CreateCachedDetector(cache, configuration);

        Assert.Equal(original, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        configuration.Set(null, "AreaLabelDetector.ReasoningEffort", effort);
        Assert.Equal(alternative, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        configuration.Remove(null, "AreaLabelDetector.ReasoningEffort");
        Assert.Equal(original, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));
    }

    [Fact]
    public void OutputBudgetAppliesToAllPredictionOptions()
    {
        var configuration = new TestConfigurationService();
        configuration.Set(null, "AreaLabelDetector.ReasoningEffort", "high");
        using var detector = new AreaLabelDetector(null!, null!, null!, null!, null!, null!, configuration, null!, null!);

        var options = detector.CreateChatCompletionOptions();
        Assert.Equal(32_768, options.MaxOutputTokenCount);
        Assert.Equal(AreaLabelDetector.MaxOutputTokens, options.MaxOutputTokenCount);

#pragma warning disable OPENAI001
        Assert.Equal("high", options.ReasoningEffortLevel.ToString());
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData("")]
    [InlineData("issue title, body, labels, and retrieved examples")]
    [InlineData("\u00e9\u4e2d\ud83d\ude00")]
    public void ReservationIncludesTokenizedPromptSchemaAndMaximumOutput(string prompt)
    {
        Assert.Equal(GitHubSemanticSearchIngestionService.Tokenizer.CountTokens(prompt) + 2_048 + AreaLabelDetector.MaxOutputTokens,
            AreaLabelDetector.EstimateTokenBudget(prompt));
        Assert.Equal(800_000, AreaLabelDetector.TokensPerMinute);
    }

    [Fact]
    public void PromptReservationCountsTokensRatherThanBytes()
    {
        Assert.Equal(2 + 2_048 + AreaLabelDetector.MaxOutputTokens, AreaLabelDetector.EstimateTokenBudget("hello world"));
    }

    [Theory]
    [InlineData(100L, 20L, null, 120)]
    [InlineData(100L, 20L, 120L, 120)]
    [InlineData(null, null, 120L, 120)]
    [InlineData(100L, null, 120L, 120)]
    [InlineData(0L, 0L, null, 0)]
    [InlineData(null, null, null, null)]
    [InlineData(100L, null, null, null)]
    [InlineData(null, 20L, null, null)]
    public void ActualTokenCountUsesReportedTotalOrCompleteInputAndOutput(long? input, long? output, long? total, int? expected)
    {
        var usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = total };

        Assert.Equal(expected, AreaLabelDetector.GetActualTokenCount(usage));
    }

    [Fact]
    public void MissingUsageDoesNotRefundTheReservation()
    {
        Assert.Null(AreaLabelDetector.GetActualTokenCount(null!));
    }

    [Fact]
    public void SuggestionsAreCanonicalDistinctValidAndOrdered()
    {
        AreaLabelSuggestion[] suggestions =
        [
            new("area-a", 0.5),
            new("AREA-A", 0.9),
            new("area-b", 1),
            new("area-c", 0.49),
            new("area-d", double.NaN),
            new("area-e", double.PositiveInfinity),
            new("area-f", 1.01),
            new("area-g", null),
            new("area-unknown", 1),
            new(null!, 1),
            null!,
        ];

        var result = AreaLabelDetector.FilterSuggestions(suggestions,
            ["area-a", "area-b", "area-c", "area-d", "area-e", "area-f", "area-g"]);

        Assert.Equal([new AreaLabelSuggestion("area-b", 1), new AreaLabelSuggestion("area-a", 0.9)], result);
    }

    [Fact]
    public void SuggestionsIncludeBoundaryAndAreLimitedToFive()
    {
        string[] labels = Enumerable.Range(0, 7).Select(i => $"area-{i}").ToArray();
        var result = AreaLabelDetector.FilterSuggestions(labels.Select(l => new AreaLabelSuggestion(l, 0.5)), labels);
        Assert.Equal(5, result.Length);
        Assert.All(result, s => Assert.Equal(0.5, s.Confidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTrackedIssueUsesTriageFallback(bool pullRequest)
    {
        var repository = new RepositoryInfo
        {
            Id = 42, FullName = "dotnet/runtime",
            Labels = [new() { Name = "needs-area-label" }, new() { Name = "area-Test" }]
        };
        var githubIssue = new SimpleJsonSerializer().Deserialize<Issue>($$"""
            {
                "node_id": "I_123",
                "number": 123,
                "title": "Current title",
                "body": "Current body",
                "state": "open",
                "created_at": "2026-09-11T12:00:00Z",
                "user": {"id": 7, "login": "author"},
                "labels": [{"name": "needs-area-label"}],
                "html_url": "https://github.com/dotnet/runtime/{{(pullRequest ? "pull" : "issues")}}/123",
                "pull_request": {{(pullRequest ? "{}" : "null")}},
                "reactions": {}
            }
            """);
        int fetches = 0;
        using var cts = new CancellationTokenSource();
        var issue = await IssueTriageHelper.GetOrFetchIssueAsync(
            repository,
            ct =>
            {
                Assert.Equal(cts.Token, ct);
                return Task.FromResult<IssueInfo>(null!);
            },
            ct =>
            {
                Assert.Equal(cts.Token, ct);
                fetches++;
                return Task.FromResult(githubIssue);
            },
            cts.Token);

        Assert.Equal(1, fetches);
        Assert.Same(repository, issue.Repository);
        Assert.Equal(42, issue.RepositoryId);
        Assert.Equal("I_123", issue.Id);
        Assert.Equal(123, issue.Number);
        Assert.Equal("Current title", issue.Title);
        Assert.Equal("Current body", issue.Body);
        Assert.Equal("author", issue.User.Login);
        Assert.Equal(7, issue.UserId);
        Assert.Equal(pullRequest ? IssueType.PullRequest : IssueType.Issue, issue.IssueType);
        Assert.Equal("needs-area-label", Assert.Single(issue.Labels).Name);
        Assert.Empty(issue.Comments);
        Assert.Contains(issue.Repository.Labels, l => l.Name == "area-Test");
    }

    [Theory]
    [InlineData(IssueType.Issue)]
    [InlineData(IssueType.PullRequest)]
    [InlineData(IssueType.Discussion)]
    public async Task StoredItemDoesNotFetchFromGitHub(IssueType issueType)
    {
        var repository = new RepositoryInfo { Id = 42, FullName = "dotnet/runtime", Labels = [new() { Name = "area-Test" }] };
        var stored = new IssueInfo { Number = 123, IssueType = issueType, Comments = [new() { Body = "Ingested comment" }] };

        var issue = await IssueTriageHelper.GetOrFetchIssueAsync(repository,
            _ => Task.FromResult(stored),
            _ => throw new InvalidOperationException("Must use the stored issue."),
            CancellationToken.None);

        Assert.Same(stored, issue);
        Assert.Same(repository, issue.Repository);
        Assert.Equal("Ingested comment", Assert.Single(issue.Comments).Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrackedOrPrivateRepositoryIsRejectedBeforeLoadingIssue(bool isPrivate)
    {
        RepositoryInfo? repository = isPrivate ? new() { Private = true } : null;
        await Assert.ThrowsAsync<NotFoundException>(() => IssueTriageHelper.GetOrFetchIssueAsync(repository!,
            _ => throw new InvalidOperationException("Must not read stored issue data."),
            _ => throw new InvalidOperationException("Must not fetch from GitHub."),
            CancellationToken.None));
    }

    [Fact]
    public async Task MissingItemPropagatesGitHubNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => IssueTriageHelper.GetOrFetchIssueAsync(
            new RepositoryInfo { Id = 42, FullName = "dotnet/runtime" },
            _ => Task.FromResult<IssueInfo>(null!),
            _ => throw new NotFoundException("Issue not found.", System.Net.HttpStatusCode.NotFound),
            CancellationToken.None));
    }
}
