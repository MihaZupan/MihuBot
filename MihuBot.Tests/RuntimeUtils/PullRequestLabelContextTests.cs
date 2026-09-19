using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.AI;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;
using Octokit.Internal;
using static MihuBot.RuntimeUtils.AI.PullRequestLabelContext;
using static MihuBot.RuntimeUtils.DataIngestion.GitHub.GitHubGraphQL;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class PullRequestLabelContextTests
{
    [Theory]
    [InlineData(IssueType.Issue, "issue")]
    [InlineData(IssueType.PullRequest, "PR")]
    [InlineData(IssueType.Discussion, "discussion")]
    public async Task PromptNamesTheActualTypeAndDoesNotMentionAbsentSessionPrompts(IssueType type, string expected)
    {
        using var transport = new Transport();
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);
        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [],
            type == IssueType.PullRequest ? context : null!, issueType: type);

        Assert.Contains($"Here is the {expected} info:", prompt, StringComparison.Ordinal);
        Assert.Contains($"labels best match the new {expected}.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("item info", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("session prompts", prompt, StringComparison.OrdinalIgnoreCase);

        if (type == IssueType.PullRequest)
        {
            Assert.Contains("Area labels on issues this PR closes are usually strong indicators", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SimilarityQueryIncludesAtMostFiftyChangedFilePaths()
    {
        using var transport = new Transport();
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);
        context = context with
        {
            Files = [.. Enumerable.Range(0, 51).Select(i => new FileEvidence($"src/file-{i}.cs", null!, "modified", 1, 0, "", false))],
        };

        string query = AreaLabelDetector.CreateSimilarityQuery(Target(), context);

        Assert.Contains("src/file-49.cs", query, StringComparison.Ordinal);
        Assert.DoesNotContain("src/file-50.cs", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RestFetchedPrWithoutStoredPrMetadataCanGatherEvidence(HttpStatusCode sessionStatus)
    {
        var repository = new RepositoryInfo { Id = 123, FullName = "dotnet/runtime", Labels = [new() { Name = "area-VM" }] };
        var restIssue = new SimpleJsonSerializer().Deserialize<Issue>("""
            {
              "node_id":"REST_PR_ID","number":42,"html_url":"https://github.com/dotnet/runtime/pull/42",
              "title":"Initial plan","body":"","state":"open","created_at":"2026-09-19T12:00:00Z",
              "user":{"id":7,"login":"contributor"},"labels":[],"pull_request":{},"reactions":{}
            }
            """);
        var issue = await IssueTriageHelper.GetOrFetchIssueAsync(repository,
            _ => Task.FromResult<IssueInfo>(null!), _ => Task.FromResult(restIssue), CancellationToken.None);
        using var transport = new Transport { Empty = true, ApiTask = true, NumericTaskArtifact = true, TaskStatus = sessionStatus };
        var context = await transport.Service.GetAsync(issue, ["area-VM"], CancellationToken.None);
        string prompt = AreaLabelDetector.CreatePrompt(
            await IssueInfoForPrompt.CreateAsync(issue, null, CancellationToken.None), ["area-VM"], "area-", [], context);

        Assert.Equal(IssueType.PullRequest, issue.IssueType);
        Assert.Null(issue.PullRequest);
        Assert.Empty(context.Files);
        Assert.Equal("contributor", context.PullRequest.Author?.Login);
        Assert.Single(context.ClosingIssues);
        Assert.Contains("Current PR title", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Initial plan", prompt, StringComparison.Ordinal);
        Assert.Equal(sessionStatus == HttpStatusCode.OK, context.CopilotSessionPrompts.Length == 1);
    }

    [Fact]
    public async Task RestStartedBranchTaskCanSupplyPromptsForAHumanAuthoredPr()
    {
        using var transport = new Transport
        {
            Empty = true,
            TaskListJson = """{"tasks":[{"id":"task-id","artifacts":[{"type":"branch","data":{"head_ref":"refs/heads/feature/fix","base_ref":"refs/heads/main"}}]}]}""",
        };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Equal("contributor", context.PullRequest.Author?.Login);
        Assert.Equal("Fix VM allocation", Assert.Single(context.CopilotSessionPrompts).Prompt);
    }

    [Fact]
    public async Task FileReferenceAndSessionQueriesRunConcurrently()
    {
        var started = new ConcurrentDictionary<string, byte>();
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport
        {
            Body = "#2", ApiTask = true,
            BeforeResponse = async (stage, ct) =>
            {
                if (stage is "files" or "references" or "tasks")
                {
                    started.TryAdd(stage, 0);

                    if (started.Count == 3)
                    {
                        allStarted.TrySetResult();
                    }

                    await release.Task.WaitAsync(ct);
                }
            },
        };
        var request = transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(request.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var context = await request;
        Assert.Single(context.Files);
        Assert.Single(context.MentionedItems);
        Assert.Single(context.CopilotSessionPrompts);
    }

    [Fact]
    public async Task TaskDetailsRunConcurrentlyAndKeepPromptsWhenAnotherTaskIsForbidden()
    {
        int started = 0;
        TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport
        {
            Empty = true,
            TaskListJson = """{"tasks":[{"id":"one","artifacts":[{"type":"pull","data":{"global_id":"TARGET"}}]},{"id":"two","artifacts":[{"type":"pull","data":{"global_id":"TARGET"}}]}]}""",
            TaskDetails = new()
            {
                ["one"] = (HttpStatusCode.OK, """{"sessions":[{"created_at":"2026-09-19T00:00:00Z","prompt":"Visible prompt"}]}"""),
                ["two"] = (HttpStatusCode.Forbidden, """{"message":"Agent tasks access denied"}"""),
            },
            BeforeResponse = async (stage, ct) =>
            {
                if (stage.StartsWith("task/", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref started) == 2)
                    {
                        bothStarted.TrySetResult();
                    }

                    await release.Task.WaitAsync(ct);
                }
            },
        };
        var request = transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var context = await request;
        Assert.Equal("Visible prompt", Assert.Single(context.CopilotSessionPrompts).Prompt);
        Assert.Contains("partially unavailable", context.CopilotSessionStatus, StringComparison.Ordinal);
        Assert.Contains("1/2", context.CopilotSessionStatus, StringComparison.Ordinal);
        Assert.Contains("HTTP 403", context.CopilotSessionStatus, StringComparison.Ordinal);
        Assert.Single(context.ClosingIssues);

        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context);
        Assert.Contains("Visible prompt", prompt, StringComparison.Ordinal);
        Assert.Contains("Copilot session prompts:", prompt, StringComparison.Ordinal);
        Assert.Contains("These describe intended work, not necessarily changes already made.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CopilotSessionStatus", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP 403", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("partially unavailable", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AccessDeniedOnTaskDetailsDoesNotDiscardPublicEvidence(HttpStatusCode status)
    {
        using var transport = new Transport
        {
            Empty = true, ApiTask = true,
            TaskDetails = new() { ["task-id"] = (status, """{"message":"Task inaccessible"}""") },
        };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Empty(context.CopilotSessionPrompts);
        Assert.Single(context.ClosingIssues);
        Assert.Contains("unavailable", context.CopilotSessionStatus, StringComparison.Ordinal);
        Assert.Contains($"HTTP {(int)status}", context.CopilotSessionStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("timeout-cancellation")]
    public async Task SessionTransportFailuresDoNotPreventPredictionEvidence(string failure)
    {
        using var transport = new Transport
        {
            Empty = true, ApiTask = true,
            BeforeResponse = (stage, _) => stage == "tasks" ? Task.FromException(failure switch
            {
                "http" => new HttpRequestException("Connection failed"),
                "timeout" => new TimeoutException("Request timed out"),
                _ => new OperationCanceledException("Internal HTTP timeout"),
            }) : Task.CompletedTask,
        };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Empty(context.CopilotSessionPrompts);
        Assert.Single(context.ClosingIssues);
        Assert.Contains("unavailable", context.CopilotSessionStatus, StringComparison.Ordinal);
        Assert.Contains(transport.Logs, log => log.StartsWith("Copilot session prompts unavailable", StringComparison.Ordinal));

        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context);
        Assert.DoesNotContain("Copilot session prompts:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(context.CopilotSessionStatus, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CopilotSessionStatus", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tasks":null}""")]
    [InlineData("""{"tasks":[null]}""")]
    [InlineData("""{"tasks":[{}]}""")]
    public async Task IncompleteTaskDiscoveryResponsesAreReportedWithoutFailingPrEvidence(string json)
    {
        using var transport = new Transport { Empty = true, TaskListJson = json };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Empty(context.CopilotSessionPrompts);
        Assert.Contains("incomplete Copilot task data", context.CopilotSessionStatus, StringComparison.Ordinal);
        Assert.Single(context.ClosingIssues);
    }

    [Fact]
    public async Task CallerCancellationIsNotMisreportedAsSessionAccessFailure()
    {
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport
        {
            Empty = true, ApiTask = true,
            BeforeResponse = async (stage, ct) =>
            {
                if (stage == "tasks")
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            },
        };
        var request = transport.Service.GetAsync(Target(), ["area-VM"], cancellation.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.DoesNotContain(transport.Logs, log => log.StartsWith("Copilot session prompts unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequiredFileLookupFailuresStillPropagate()
    {
        using var transport = new Transport
        {
            Body = "#2",
            BeforeResponse = (stage, _) => stage == "files"
                ? Task.FromException(new HttpRequestException("File lookup failed"))
                : Task.CompletedTask,
        };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None));

        Assert.Equal("File lookup failed", error.Message);
        Assert.Contains(transport.GraphRequests, r => r.GetProperty("query").GetString()!.Contains("ReferencedLabelItems", StringComparison.Ordinal));
        Assert.Contains(transport.RestRequests, r => r.AbsolutePath.EndsWith("/tasks", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuesAndPrsUseSharedBodyAndCommentLimits(bool pullRequest)
    {
        using var transport = new Transport();
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);
        context = context with
        {
            PullRequest = context.PullRequest with
            {
                Body = new string('b', 32_001),
                Comments = new([new(new string('c', 4001), new("author"), DateTime.UtcNow)]),
            },
        };
        var input = (await PromptItem()) with
        {
            Body = new string('b', 32_001),
            Comments = [new("author", null, DateTime.UtcNow, new string('c', 4001), null)],
        };
        string prompt = AreaLabelDetector.CreatePrompt(input, ["area-VM"], "area-", [], pullRequest ? context : null!);
        using var item = JsonDocument.Parse(prompt.Split("```json", 2, StringSplitOptions.None)[1].Split("```", 2, StringSplitOptions.None)[0]);

        Assert.Equal(32_000, item.RootElement.GetProperty("Body").GetString()!.Length);
        Assert.Equal(4000, item.RootElement.GetProperty("Comments")[0].GetProperty("Body").GetString()!.Length);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(5, true)]
    public async Task AuthorHistoryIsIncludedOnlyWhenAvailableAndNotesCommonAreas(int count, bool common)
    {
        using var transport = new Transport();
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);
        var history = AreaLabelDetector.AnalyzeAuthorHistory("contributor",
            [.. Enumerable.Range(1, count).Select(n => new AreaLabelDetector.AuthorPullRequest(
                n, $"https://github.com/dotnet/runtime/pull/{n}", "Author's previous PR", DateTime.UtcNow.AddDays(-1), "area-VM"))],
            ["area-VM"]);
        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context, history);

        Assert.Equal(count > 0, prompt.Contains("Recent PRs by this author in this repository:", StringComparison.Ordinal));
        Assert.Equal(common, prompt.Contains("Common areas in this author's prior PRs", StringComparison.Ordinal));
        Assert.Contains("Historical PR", prompt, StringComparison.Ordinal);
        Assert.Contains("Linked issue body", prompt, StringComparison.Ordinal);

        if (count > 0)
        {
            Assert.DoesNotContain("local ingested database", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("preceding year", prompt, StringComparison.Ordinal);
            Assert.Contains($"\"SampledPullRequests\":{count}", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("LabeledPullRequests", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("Counts use active candidate labels", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("a bot author is not an area", prompt, StringComparison.Ordinal);
        }

        string issuePrompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], null!, history);
        Assert.DoesNotContain("Recent PRs by this author", issuePrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bot")]
    [InlineData("Mannequin")]
    [InlineData("Organization")]
    [InlineData(null)]
    public async Task NonUserAuthorsNeverReceiveAnAuthorHistorySection(string? authorType)
    {
        using var transport = new Transport { AuthorType = authorType };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);
        var history = AreaLabelDetector.AnalyzeAuthorHistory("contributor",
            [.. Enumerable.Range(1, 10).Select(n => new AreaLabelDetector.AuthorPullRequest(
                n, $"https://github.com/dotnet/runtime/pull/{n}", "Author-specific previous PR", DateTime.UtcNow.AddDays(-1), "area-VM"))],
            ["area-VM"]);
        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context, history);

        Assert.DoesNotContain("Recent PRs by this author", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Author-specific previous PR", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Common areas in this author's prior PRs", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SemanticSectionIsConditionalAndCoexistsWithPrEvidence(bool hasSimilar)
    {
        using var transport = new Transport();
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);
        AreaLabelDetector.SimilarIssue[] similar = hasSimilar
            ? [new("https://github.com/dotnet/runtime/issues/55", "other-author", "Strong semantic match", "Similar issue body", ["area-VM"])]
            : [];

        foreach (var prContext in new Context?[] { context, null })
        {
            string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", similar, prContext!);

            Assert.Equal(hasSimilar, prompt.Contains("Semantically similar issues and PRs", StringComparison.Ordinal));
            Assert.Equal(hasSimilar, prompt.Contains("Strong semantic match", StringComparison.Ordinal));

            if (prContext is not null)
            {
                Assert.Contains("Historical PR", prompt, StringComparison.Ordinal);
                Assert.Contains("Linked issue body", prompt, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void DescriptionReferencesExcludeClosuresSelfAndCaseInsensitiveDuplicates()
    {
        var references = GetDescriptionReferences("""
            Fixes #1. Related: #2, DOTNET/RUNTIME#2,
            [PR](https://github.com/dotnet/runtime/pull/2/files), #42,
            dotnet/other#2 and https://github.com/dotnet/other/issues/3#issuecomment-99.
            """, "dotnet/runtime", 42, ["https://github.com/DOTNET/RUNTIME/issues/1"]);

        Assert.Equal(new[] { ("dotnet/runtime", 2), ("dotnet/other", 2), ("dotnet/other", 3) }, references);
    }

    [Fact]
    public async Task MentionedIssuesAndPrsAreFetchedOnceAndUseSharedTextLimits()
    {
        using var transport = new Transport
        {
            Body = "Fixes #1; see #2, #3, DOTNET/RUNTIME#2 and dotnet/other#5. Not #42.",
            ReferencedBody = new string('b', 5000),
            ReferencedTitle = new string('t', 300),
        };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Equal(3, context.MentionedItems.Length);
        Assert.Equal(
            ["https://github.com/dotnet/runtime/pull/2", "https://github.com/dotnet/runtime/issues/3", "https://github.com/dotnet/other/issues/5"],
            context.MentionedItems.Select(i => i.Url));
        Assert.All(context.MentionedItems, item =>
        {
            Assert.Equal(AreaLabelDetector.MaxContextBodyCharacters, item.Body.Length);
            Assert.Equal(AreaLabelDetector.MaxRelatedTitleCharacters, item.Title.Length);
        });
        Assert.Equal(["area-VM"], context.MentionedItems[0].Labels);
        Assert.Equal(["area-VM", "other-label"], context.MentionedItems[2].Labels);
        Assert.Equal(3, transport.GraphRequests.Count);
        Assert.Contains("3 GraphQL API calls, cost 11.", Assert.Single(transport.Logs), StringComparison.Ordinal);
        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context);
        Assert.Contains("\"MentionedItems\"", prompt, StringComparison.Ordinal);
        Assert.Contains("https://github.com/dotnet/runtime/pull/2", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ClosingIssuesTruncated", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MentionedItemsTruncated", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MentionedItemSamplingRemainsBounded()
    {
        using var transport = new Transport { Body = string.Join(' ', Enumerable.Range(100, 30).Select(n => $"#{n}")) };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Equal(ReferencedLabelItemsBatchSize, context.MentionedItems.Length);
        var references = transport.GraphRequests.Single(r => r.GetProperty("query").GetString()!.Contains("query ReferencedLabelItems(", StringComparison.Ordinal));
        Assert.Equal(ReferencedLabelItemsBatchSize * 3, references.GetProperty("variables").EnumerateObject().Count());
    }

    [Fact]
    public async Task ReferencesThatRedirectToClosingIssuesAreNotRepeated()
    {
        using var transport = new Transport { Body = "#99", ReferenceUrlOverride = "https://github.com/dotnet/runtime/issues/1" };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Single(context.ClosingIssues);
        Assert.Empty(context.MentionedItems);
    }

    [Fact]
    public async Task LiveChangesUseFileHistoryAndClosingIssuesInsteadOfStaleDescription()
    {
        using var transport = new Transport();
        var context = await transport.Service.GetAsync(Target(), ["area-VM", "area-JIT"], CancellationToken.None);

        Assert.Equal("Current PR title", context.PullRequest.Title);
        Assert.Equal("", context.PullRequest.Body);
        Assert.Equal("src/vm/file.cpp", Assert.Single(context.Files).Path);
        Assert.Contains("+new code", context.Files[0].Patch, StringComparison.Ordinal);
        Assert.Equal("area-VM", Assert.Single(Assert.Single(context.ClosingIssues).Labels));
        var example = Assert.Single(context.HistoricalPullRequests);
        Assert.Equal(["area-VM", "area-JIT"], example.Labels);
        Assert.Equal(new HistoryPath("src/vm/file.cpp", false), Assert.Single(example.MatchingPaths));
        Assert.Equal("base-sha", transport.GraphRequests[1].GetProperty("variables").GetProperty("base").GetString());
        Assert.Contains(transport.RestRequests, uri => uri.AbsolutePath.EndsWith("/tasks", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.RestRequests, uri => uri.AbsolutePath.Contains("/tasks/", StringComparison.Ordinal));
        Assert.Contains("2 GraphQL API calls, cost 7.", Assert.Single(transport.Logs), StringComparison.Ordinal);

        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM", "area-JIT"], "area-", [], context);
        Assert.Contains("Current PR title", prompt, StringComparison.Ordinal);
        Assert.Contains("new code", prompt, StringComparison.Ordinal);
        Assert.Contains("Linked issue body", prompt, StringComparison.Ordinal);
        Assert.Contains("Historical PR", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale title", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale body", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("area-LeakedTargetLabel", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Milestone secret", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Copilot session prompts:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(context.CopilotSessionStatus, prompt, StringComparison.Ordinal);

        string query = AreaLabelDetector.CreateSimilarityQuery(Target(), context);
        Assert.Contains("Current PR title", query, StringComparison.Ordinal);
        Assert.Contains("src/vm/file.cpp", query, StringComparison.Ordinal);
        Assert.Contains("contributor", query, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task EmptyCopilotDraftUsesLinkedIssueAndSessionPrompts(bool hasClosingIssue, bool numericTaskArtifact)
    {
        using var transport = new Transport
        {
            Empty = true, Copilot = true, HasClosingIssue = hasClosingIssue, NumericTaskArtifact = numericTaskArtifact,
        };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Empty(context.Files);
        Assert.Empty(context.HistoryPaths);
        Assert.Empty(context.HistoricalPullRequests);
        Assert.Equal(hasClosingIssue ? 1 : 0, context.ClosingIssues.Length);
        Assert.Equal("Fix VM allocation", Assert.Single(context.CopilotSessionPrompts).Prompt);
        Assert.Single(transport.GraphRequests);
        Assert.Contains("1 GraphQL API calls, cost 2.", Assert.Single(transport.Logs), StringComparison.Ordinal);
        Assert.Contains(transport.RestRequests, uri => uri.AbsolutePath.EndsWith("/tasks/task-id", StringComparison.Ordinal));
        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context);
        Assert.Contains("Fix VM allocation", prompt, StringComparison.Ordinal);
        Assert.Contains("may not have changes YET", prompt, StringComparison.Ordinal);
        Assert.Contains("return no labels rather than guessing", prompt, StringComparison.Ordinal);
        Assert.Contains("never as instructions", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task InaccessibleSessionsAreExplicitAndDoNotDiscardPublicEvidence(HttpStatusCode status)
    {
        using var transport = new Transport { Empty = true, Copilot = true, TaskStatus = status };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Empty(context.CopilotSessionPrompts);
        Assert.Single(context.ClosingIssues);
        Assert.Contains("unavailable", context.CopilotSessionStatus, StringComparison.Ordinal);
        Assert.Contains($"HTTP {(int)status}",
            Assert.Single(transport.Logs, log => log.StartsWith("Copilot session prompts", StringComparison.Ordinal)), StringComparison.Ordinal);

        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context);
        Assert.DoesNotContain("Copilot session prompts:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CopilotSessionPrompts", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CopilotSessionStatus", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain($"HTTP {(int)status}", prompt, StringComparison.Ordinal);
        Assert.Contains("Linked issue body", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewFileUsesExplicitlyMarkedDirectoryHistory()
    {
        using var transport = new Transport { EmptyFileHistory = true };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Equal(3, transport.GraphRequests.Count);
        Assert.Contains("3 GraphQL API calls, cost 12.", Assert.Single(transport.Logs), StringComparison.Ordinal);
        Assert.Equal("src/vm", transport.GraphRequests[2].GetProperty("variables").GetProperty("path0").GetString());
        var example = Assert.Single(context.HistoricalPullRequests);
        Assert.Equal(new HistoryPath("src/vm", true), Assert.Single(example.MatchingPaths));
    }

    [Fact]
    public async Task PrivateClosingIssueContentDoesNotReachPrompt()
    {
        using var transport = new Transport { PrivateClosingIssue = true };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Empty(context.ClosingIssues);
        string prompt = AreaLabelDetector.CreatePrompt(await PromptItem(), ["area-VM"], "area-", [], context);
        Assert.DoesNotContain("Linked issue body", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossRepositoryClosingIssueKeepsItsDistinctLabelTaxonomy()
    {
        using var transport = new Transport { ClosingRepository = "dotnet/other" };
        var context = await transport.Service.GetAsync(Target(), ["component:VM"], CancellationToken.None);

        Assert.Equal(["area-VM", "unrelated"], Assert.Single(context.ClosingIssues).Labels);
        Assert.Empty(context.HistoricalPullRequests);
    }

    [Fact]
    public async Task PrivateTargetIsRejectedBeforeRestRequests()
    {
        using var transport = new Transport { PrivateRepository = true };

        await Assert.ThrowsAsync<NotFoundException>(() => transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None));
        Assert.Empty(transport.RestRequests);
    }

    [Fact]
    public async Task CancellationStopsEvidenceCollection()
    {
        using var transport = new Transport();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.Service.GetAsync(Target(), ["area-VM"], cts.Token));
        Assert.Empty(transport.RestRequests);
    }

    [Fact]
    public async Task FilePaginationAndTruncationAreExplicitlyBounded()
    {
        using var transport = new Transport { PaginateFiles = true };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Equal(3, transport.RestRequests.Count(uri => uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)));
        Assert.Equal(MaxFiles, context.Files.Length);
        Assert.True(context.FilesTruncated);
        Assert.Equal(MaxHistoryPaths, context.HistoryPaths.Length);
        Assert.True(context.Files.Sum(f => f.Patch.Length) <= MaxPatchCharacters);
    }

    [Fact]
    public void PatchesAreBoundedAndRenamesBinaryFilesAndDeletionsRemainVisible()
    {
        var serializer = new SimpleJsonSerializer();
        var files = serializer.Deserialize<PullRequestFile[]>("""
            [
              {"filename":"new/name.cpp","previous_filename":"old/name.cpp","status":"renamed","additions":1,"deletions":1,"changes":2,"patch":"+new"},
              {"filename":"image.png","status":"added","additions":0,"deletions":0,"changes":0},
              {"filename":"removed.cs","status":"removed","additions":0,"deletions":4,"changes":4,"patch":"-old code"}
            ]
            """);
        var evidence = CreateFileEvidence(files);

        Assert.Equal("old/name.cpp", evidence.Single(f => f.Status == "renamed").PreviousPath);
        Assert.True(evidence.Single(f => f.Path == "image.png").PatchIncomplete);
        Assert.Equal("-old code", evidence.Single(f => f.Status == "removed").Patch);
        Assert.Contains(new HistoryPath("old/name.cpp", false), SelectHistoryPaths(evidence));
    }

    [Fact]
    public void HistorySamplingPreservesCaseAndDirectoryDiversity()
    {
        FileEvidence[] files = [
            .. Enumerable.Range(0, 30).Select(i => new FileEvidence($"generated/{i}.cs", null!, "modified", 100, 0, "", false)),
            new("src/Area.cs", null!, "modified", 1, 0, "", false),
            new("src/area.cs", null!, "modified", 1, 0, "", false),
        ];
        var paths = SelectHistoryPaths(files);

        Assert.Equal(MaxHistoryPaths, paths.Length);
        Assert.Contains(new HistoryPath("src/Area.cs", false), paths);
        Assert.Contains(new HistoryPath("src/area.cs", false), paths);
    }

    [Fact]
    public void HistorySamplingRoundRobinsDirectoriesBeforeApplyingTheLimit()
    {
        string[] input =
        [
            .. Enumerable.Range(1, 10).Select(i => $"generated/g{i}.cs"),
            "src/r1.cs", "src/r2.cs", "tests/t1.cs", "tests/t2.cs",
        ];
        var files = input.Select(p => new FileEvidence(p, null!, "modified", 1, 0, "", false)).ToArray();

        Assert.Equal(
        [
            "generated/g1.cs", "src/r1.cs", "tests/t1.cs",
            "generated/g2.cs", "src/r2.cs", "tests/t2.cs",
            "generated/g3.cs", "generated/g4.cs", "generated/g5.cs",
            "generated/g6.cs", "generated/g7.cs", "generated/g8.cs",
        ], SelectHistoryPaths(files).Select(p => p.Path));
    }

    [Fact]
    public void SingleFilePatchUsesPerFileCharacterLimit()
    {
        var file = new SimpleJsonSerializer().Deserialize<PullRequestFile>(JsonSerializer.Serialize(new
        {
            filename = "src/file.cs", changes = 1, patch = new string('x', MaxPatchCharactersPerFile + 1),
        }));
        var evidence = Assert.Single(CreateFileEvidence([file]));

        Assert.Equal(MaxPatchCharactersPerFile, evidence.Patch.Length);
        Assert.True(evidence.PatchIncomplete);
    }

    [Fact]
    public async Task LinkedIssueBodyUsesItsCharacterLimit()
    {
        using var transport = new Transport { LinkedIssueBody = new string('x', AreaLabelDetector.MaxContextBodyCharacters + 1) };
        var context = await transport.Service.GetAsync(Target(), ["area-VM"], CancellationToken.None);

        Assert.Equal(AreaLabelDetector.MaxContextBodyCharacters, Assert.Single(context.ClosingIssues).Body.Length);
    }

    [Fact]
    public void HistoryRanksDistinctExactOverlapAboveDirectoryRecencyAndExcludesIneligibleExamples()
    {
        PullRequestHistoryModel Example(string url, string repo = "dotnet/runtime", bool isPrivate = false, string label = "area-VM") =>
            new(url, url, "", new("author"), new(repo, isPrivate), new([new(label), new("area-JIT")]), DateTime.UtcNow);
        var overlap = Example("overlap");
        var directory = Example("directory");
        var exact = Example("exact");
        HistoryMatch[] matches = [
            new(new("src/a", true), directory),
            new(new("src/a.cs", false), exact),
            new(new("src/a.cs", false), exact),
            new(new("src/a.cs", false), overlap),
            new(new("src/b.cs", false), overlap),
            new(new("src/a.cs", false), Example("self")),
            new(new("src/a.cs", false), Example("foreign", repo: "other/repo")),
            new(new("src/a.cs", false), Example("private", isPrivate: true)),
            new(new("src/a.cs", false), Example("unmerged") with { MergedAt = null }),
            new(new("src/a.cs", false), Example("unlabeled") with { Labels = new([]) }),
        ];
        var result = RankHistory(matches, "self", "DOTNET/RUNTIME", ["area-VM", "area-JIT"]);

        Assert.Equal(["overlap", "exact", "directory"], result.Select(r => r.Url));
        Assert.Equal(2, result[0].MatchingPaths.Length);
        Assert.Single(result[1].MatchingPaths);
        Assert.Equal(["area-VM", "area-JIT"], result[0].Labels);
    }

    [Fact]
    public void HistoricalPrsUseTheSameTitleAndBodyCutoffsAsRelatedIssues()
    {
        var pr = new PullRequestHistoryModel("historical", new string('t', 300), new string('b', 5000),
            new("author"), new("dotnet/runtime", false), new([new("area-VM")]), DateTime.UtcNow);
        var result = Assert.Single(RankHistory([new(new("src/file.cs", false), pr)], "target", "dotnet/runtime", ["area-VM"]));

        Assert.Equal(200, result.Title.Length);
        Assert.Equal(4000, result.Body.Length);
    }

    [Fact]
    public void TaskMatchingDoesNotTrustUnrelatedTasksOrBranches()
    {
        var tasks = new SimpleJsonSerializer().Deserialize<CopilotTaskCollection>("""
            {"tasks":[
              {"id":"other","artifacts":[{"type":"pull","data":{"global_id":"OTHER"}}]},
              {"id":"branch","artifacts":[{"type":"branch","data":{"head_ref":"copilot/test"}}]},
              {"id":"numeric","artifacts":[{"type":"pull","data":{"id":12345}}]},
              {"id":"matching","artifacts":[{"type":"pull","data":{"global_id":"TARGET"}}]},
              {"id":"matching","artifacts":[{"type":"pull","data":{"global_id":"TARGET"}}]}
            ]}
            """);

        Assert.Equal(["numeric", "matching"], FindTaskIds(tasks, TaskTarget(), "dotnet/runtime"));
    }

    [Fact]
    public void TaskMatchingPrefersPrIdsAndRejectsForkAndContradictoryBranchAssociations()
    {
        var tasks = new SimpleJsonSerializer().Deserialize<CopilotTaskCollection>("""
            {"tasks":[
              {"id":"branch","artifacts":[{"type":"branch","data":{"head_ref":"feature/fix","base_ref":"main"}}]},
              {"id":"wrong-base","artifacts":[{"type":"branch","data":{"head_ref":"feature/fix","base_ref":"release/8.0"}}]},
              {"id":"wrong-case","artifacts":[{"type":"branch","data":{"head_ref":"Feature/Fix","base_ref":"main"}}]},
              {"id":"other-pr","artifacts":[{"type":"pull","data":{"global_id":"OTHER"}},{"type":"branch","data":{"head_ref":"feature/fix","base_ref":"main"}}]},
              {"id":"direct","artifacts":[{"type":"pull","data":{"global_id":"TARGET"}}]}
            ]}
            """);
        var pr = TaskTarget();

        Assert.Equal(["direct", "branch"], FindTaskIds(tasks, pr, "dotnet/runtime"));
        Assert.Equal(["direct"], FindTaskIds(tasks, pr with { HeadRepository = new("fork/runtime", false) }, "dotnet/runtime"));
        Assert.Equal(["direct"], FindTaskIds(tasks, pr with { HeadRepository = null }, "dotnet/runtime"));
    }

    [Fact]
    public void SessionPromptsAreBoundedAndOrderedWithoutEmptyEntries()
    {
        var sessions = new JsonArray();

        for (int i = 15; i >= 1; i--)
        {
            sessions.Add(new JsonObject { ["created_at"] = $"2026-09-{i:00}T00:00:00Z", ["prompt"] = new string('x', 7000) });
        }

        sessions.Add(new JsonObject { ["prompt"] = "" });
        var response = new SimpleJsonSerializer().Deserialize<CopilotTask>(new JsonObject { ["sessions"] = sessions }.ToJsonString());
        var prompts = ReadSessionPrompts(response);

        Assert.Equal(10, prompts.Length);
        Assert.Equal(6, prompts[0].CreatedAt.Day);
        Assert.Equal(15, prompts[^1].CreatedAt.Day);
        Assert.All(prompts, p => Assert.Equal(6000, p.Prompt.Length));
    }

    private static IssueInfo Target() => new()
    {
        Id = "TARGET", Number = 42, RepositoryId = 123, IssueType = IssueType.PullRequest,
        Repository = new() { FullName = "dotnet/runtime" },
        User = new() { Login = "Stale author" },
        Title = "Stale title", Body = "Stale body",
        Labels = [new() { Name = "area-LeakedTargetLabel" }],
        Milestone = new() { Title = "Milestone secret" },
    };

    private static Task<IssueInfoForPrompt> PromptItem() => IssueInfoForPrompt.CreateAsync(Target(), null, CancellationToken.None);

    private static PullRequestLabelInfoModel TaskTarget() =>
        new("TARGET", 12345, "https://github.com/dotnet/runtime/pull/42", "", "", new("human-author"), "base-sha", true,
            0, 0, 0, new([]), new([]), "feature/fix", "main", new("dotnet/runtime", false));

    private sealed class Transport : HttpMessageHandler
    {
        private readonly HttpClient _http;
        private readonly object _lock = new();
        public PullRequestLabelContext Service { get; }
        public List<JsonElement> GraphRequests { get; } = [];
        public List<Uri> RestRequests { get; } = [];
        public List<string> Logs { get; } = [];
        public bool Empty { get; init; }
        public bool Copilot { get; init; }
        public string? AuthorType { get; init; } = "User";
        public bool ApiTask { get; init; }
        public bool HasClosingIssue { get; init; } = true;
        public bool EmptyFileHistory { get; init; }
        public bool PrivateClosingIssue { get; init; }
        public bool PrivateRepository { get; init; }
        public bool PaginateFiles { get; init; }
        public bool NumericTaskArtifact { get; init; }
        public string ClosingRepository { get; init; } = "dotnet/runtime";
        public string LinkedIssueBody { get; init; } = "Linked issue body";
        public string Body { get; init; } = "";
        public string ReferencedBody { get; init; } = "Mentioned item body";
        public string ReferencedTitle { get; init; } = "Mentioned item title";
        public string? ReferenceUrlOverride { get; init; }
        public HttpStatusCode TaskStatus { get; init; } = HttpStatusCode.OK;
        public string? TaskListJson { get; init; }
        public Dictionary<string, (HttpStatusCode Status, string Json)> TaskDetails { get; init; } = [];
        public Func<string, CancellationToken, Task>? BeforeResponse { get; init; }

        public Transport()
        {
            _http = new HttpClient(this, disposeHandler: false);
            var graph = new GithubGraphQLClient("tests", ["test-token"], NullLogger.Instance, _http);
            var rest = new GitHubClient(new Octokit.Connection(new ProductHeaderValue("tests"), new HttpClientAdapter(() => this)));
            Service = new(rest, graph, message =>
            {
                lock (_lock)
                {
                    Logs.Add(message);
                }
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;

            if (uri.AbsolutePath == "/graphql")
            {
                var json = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
                lock (_lock)
                {
                    GraphRequests.Add(json);
                }

                string query = json.GetProperty("query").GetString()!;

                if (BeforeResponse is not null)
                {
                    string stage = query.Contains("query PullRequestLabelInfo(", StringComparison.Ordinal) ? "metadata" :
                        query.Contains("query ReferencedLabelItems(", StringComparison.Ordinal) ? "references" : "history";
                    await BeforeResponse(stage, cancellationToken);
                }

                if (query.Contains("query PullRequestLabelInfo(", StringComparison.Ordinal))
                {
                    JsonArray linked = [];

                    if (HasClosingIssue)
                    {
                        linked.Add(new JsonObject
                        {
                            ["url"] = $"https://github.com/{ClosingRepository}/issues/1", ["title"] = "Linked issue",
                            ["body"] = LinkedIssueBody, ["author"] = new JsonObject { ["login"] = "reporter" },
                            ["repository"] = new JsonObject { ["nameWithOwner"] = ClosingRepository, ["isPrivate"] = PrivateClosingIssue },
                            ["labels"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["name"] = "area-VM" }, new JsonObject { ["name"] = "unrelated" }) },
                        });
                    }

                    return Json(new JsonObject
                    {
                        ["data"] = new JsonObject
                        {
                            ["rateLimit"] = new JsonObject { ["cost"] = 2 },
                            ["repository"] = new JsonObject
                            {
                                ["isPrivate"] = PrivateRepository,
                                ["pullRequest"] = new JsonObject
                                {
                                    ["id"] = "TARGET", ["databaseId"] = "4500000000", ["url"] = "https://github.com/dotnet/runtime/pull/42",
                                    ["title"] = "Current PR title", ["body"] = Body, ["isDraft"] = Empty,
                                    ["author"] = new JsonObject { ["login"] = Copilot ? "copilot-swe-agent" : "contributor", ["__typename"] = Copilot ? "Bot" : AuthorType },
                                    ["baseRefOid"] = "base-sha",
                                    ["headRefName"] = "feature/fix", ["baseRefName"] = "main",
                                    ["headRepository"] = new JsonObject { ["nameWithOwner"] = "dotnet/runtime", ["isPrivate"] = false },
                                    ["changedFiles"] = Empty ? 0 : PaginateFiles ? 301 : 1, ["additions"] = 1, ["deletions"] = 1,
                                    ["comments"] = new JsonObject { ["nodes"] = new JsonArray() },
                                    ["closingIssuesReferences"] = new JsonObject { ["nodes"] = linked },
                                },
                            },
                        },
                    });
                }

                if (query.Contains("query ReferencedLabelItems(", StringComparison.Ordinal))
                {
                    var variables = json.GetProperty("variables");
                    var data = new JsonObject { ["rateLimit"] = new JsonObject { ["cost"] = 4 } };

                    for (int i = 0; variables.TryGetProperty($"number{i}", out var number); i++)
                    {
                        string repository = $"{variables.GetProperty($"owner{i}").GetString()}/{variables.GetProperty($"name{i}").GetString()}";
                        int n = number.GetInt32();
                        data[$"reference{i}"] = new JsonObject
                        {
                            ["isPrivate"] = false,
                            ["item"] = new JsonObject
                            {
                                ["url"] = ReferenceUrlOverride ?? $"https://github.com/{repository}/{(n % 2 == 0 ? "pull" : "issues")}/{n}",
                                ["title"] = ReferencedTitle, ["body"] = ReferencedBody,
                                ["author"] = new JsonObject { ["login"] = "reference-author" },
                                ["repository"] = new JsonObject { ["nameWithOwner"] = repository, ["isPrivate"] = false },
                                ["labels"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["name"] = "area-VM" }, new JsonObject { ["name"] = "other-label" }) },
                            },
                        };
                    }

                    return Json(new JsonObject { ["data"] = data });
                }

                var histories = new JsonObject { ["rateLimit"] = new JsonObject { ["cost"] = 5 } };

                foreach (var variable in json.GetProperty("variables").EnumerateObject().Where(p => p.Name.StartsWith("path", StringComparison.Ordinal)))
                {
                    bool empty = EmptyFileHistory && variable.Value.GetString() == "src/vm/file.cpp";
                    histories[variable.Name] = JsonNode.Parse(empty ? """{"object":{"history":{"nodes":[]}}}""" : """
                        {"object":{"history":{"nodes":[{"associatedPullRequests":{"nodes":[
                          {"url":"https://github.com/dotnet/runtime/pull/1","title":"Historical PR","body":"Earlier VM change",
                           "author":{"login":"maintainer"},"repository":{"nameWithOwner":"dotnet/runtime","isPrivate":false},
                           "mergedAt":"2026-09-01T00:00:00Z","labels":{"nodes":[{"name":"area-VM"},{"name":"area-JIT"},{"name":"other"}]}}
                        ]}}]}}}
                        """);
                }

                return Json(new JsonObject { ["data"] = histories });
            }

            lock (_lock)
            {
                RestRequests.Add(uri);
            }

            if (BeforeResponse is not null)
            {
                string stage = uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal) ? "files" :
                    uri.AbsolutePath.EndsWith("/tasks", StringComparison.Ordinal) ? "tasks" : $"task/{uri.Segments[^1]}";
                await BeforeResponse(stage, cancellationToken);
            }

            if (uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                JsonArray files = [];

                for (int i = 0; i < (Empty ? 0 : PaginateFiles ? 100 : 1); i++)
                {
                    files.Add(new JsonObject
                    {
                        ["filename"] = PaginateFiles ? $"src/{uri.Query}/{i}.cs" : "src/vm/file.cpp",
                        ["status"] = "modified", ["additions"] = 1, ["deletions"] = 1, ["changes"] = 2,
                        ["patch"] = PaginateFiles ? new string('p', 5000) : "+new code",
                    });
                }

                var response = Json(files);

                if (PaginateFiles)
                {
                    int page;

                    lock (_lock)
                    {
                        page = RestRequests.Count(r => r.AbsolutePath.EndsWith("/files", StringComparison.Ordinal));
                    }

                    response.Headers.Add("Link", $"<https://api.github.com/repositories/123/pulls/42/files?page={page + 1}&per_page=100>; rel=\"next\"");
                }

                return response;
            }

            if (uri.AbsolutePath.EndsWith("/tasks", StringComparison.Ordinal))
            {
                string tasks = TaskListJson ?? (Copilot || ApiTask
                    ? NumericTaskArtifact
                        ? """{"tasks":[{"id":"task-id","artifacts":[{"type":"pull","data":{"id":4500000000}}]}]}"""
                        : """{"tasks":[{"id":"task-id","artifacts":[{"type":"pull","data":{"id":4500000000,"global_id":"TARGET"}}]}]}"""
                    : """{"tasks":[]}""");
                return Json(JsonNode.Parse(TaskStatus == HttpStatusCode.OK ? tasks : """{"message":"Task access unavailable"}""")!, TaskStatus);
            }

            if (uri.AbsolutePath.Contains("/tasks/", StringComparison.Ordinal))
            {
                if (TaskDetails.TryGetValue(uri.Segments[^1], out var detail))
                {
                    return Json(JsonNode.Parse(detail.Json)!, detail.Status);
                }

                return Json(JsonNode.Parse("""{"sessions":[{"created_at":"2026-09-01T00:00:00Z","prompt":"Fix VM allocation"}]}""")!);
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        }

        private static HttpResponseMessage Json(JsonNode json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _http.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
