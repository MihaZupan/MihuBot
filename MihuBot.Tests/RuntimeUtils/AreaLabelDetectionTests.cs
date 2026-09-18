using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.DB.GitHub;
using MihuBot.Helpers.AI;
using MihuBot.RuntimeUtils.AI;
using MihuBot.Tests.Configuration;
using Octokit;
using Octokit.Internal;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelDetectionTests
{
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
        var detector = new AreaLabelDetector(null!, null!, null!, null!, null!, null!, new TestConfigurationService());

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
        await cache.SetAsync($"AreaLabels:{OpenAIService.DefaultModel}:medium:dotnet/runtime:123:area-", area);
        await cache.SetAsync($"AreaLabels:{OpenAIService.DefaultModel}:medium:dotnet/runtime:123:component:", component);
        await cache.SetAsync($"AreaLabels:{OpenAIService.DefaultModel}:medium:dotnet/runtime:124:area-", discussion);
        var detector = new AreaLabelDetector(null!, null!, null!, null!, cache, null!, new TestConfigurationService());

        Assert.Equal(area, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));
        Assert.Equal(component, await detector.PredictAsync("DOTNET/RUNTIME", 123, "COMPONENT:", CancellationToken.None));
        Assert.Equal(discussion, await detector.PredictAsync("dotnet/runtime", 124, "area-", CancellationToken.None));
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
        await cache.SetAsync($"AreaLabels:{OpenAIService.DefaultModel}:medium:dotnet/runtime:123:area-", original);
        await cache.SetAsync($"AreaLabels:{Uri.EscapeDataString(model)}:medium:dotnet/runtime:123:area-", alternative);
        var detector = new AreaLabelDetector(null!, null!, null!, null!, cache, null!, configuration);

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
    public void ReasoningConfigurationIsAppliedToChatOptions(string effort)
    {
        var configuration = new TestConfigurationService();
        var detector = new AreaLabelDetector(null!, null!, null!, null!, null!, null!, configuration);

#pragma warning disable OPENAI001
        Assert.Equal("medium", detector.CreateChatCompletionOptions().ReasoningEffortLevel.ToString());

        configuration.Set(null, "AreaLabelDetector.ReasoningEffort", effort);
        Assert.Equal(effort, detector.CreateChatCompletionOptions().ReasoningEffortLevel.ToString());

        configuration.Remove(null, "AreaLabelDetector.ReasoningEffort");
        Assert.Equal("medium", detector.CreateChatCompletionOptions().ReasoningEffortLevel.ToString());
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData("none")]
    [InlineData("low")]
    [InlineData("high")]
    [InlineData("xhigh")]
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
        await cache.SetAsync($"AreaLabels:{OpenAIService.DefaultModel}:medium:dotnet/runtime:123:area-", original);
        await cache.SetAsync($"AreaLabels:{OpenAIService.DefaultModel}:{effort}:dotnet/runtime:123:area-", alternative);
        var detector = new AreaLabelDetector(null!, null!, null!, null!, cache, null!, configuration);

        Assert.Equal(original, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        configuration.Set(null, "AreaLabelDetector.ReasoningEffort", effort);
        Assert.Equal(alternative, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        configuration.Remove(null, "AreaLabelDetector.ReasoningEffort");
        Assert.Equal(original, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));
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
