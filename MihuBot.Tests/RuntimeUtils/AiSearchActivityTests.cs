using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.RuntimeUtils;
using MihuBot.RuntimeUtils.AI;
using MihuBot.RuntimeUtils.Search;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AiSearchActivityTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    [InlineData("handledFailure")]
    public async Task AutomaticRunRecordsOutcomeAndPreservesFailures(string outcome)
    {
        using var activities = new RecordedActivities();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception failure = outcome == "cancellation"
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Background run failed");

        var run = MihuBotAIActivitySource.RunAutomaticAsync("AutomaticTestRun", async activity =>
        {
            Assert.Same(activity, Activity.Current);
            activity.SetTag("run.phase", "processing");
            await Task.Yield();

            if (outcome == "handledFailure")
            {
                activity.SetError(failure);
            }
            else if (outcome != "success")
            {
                throw failure;
            }
        });

        if (outcome is "failure" or "cancellation")
        {
            Assert.Same(failure, await Record.ExceptionAsync(() => run));
        }
        else
        {
            await run;
        }

        var activity = Assert.Single(activities.Stopped);
        Assert.Equal("AutomaticTestRun", activity.OperationName);
        Assert.Equal(activities.Root.SpanId, activity.ParentSpanId);
        Assert.Equal("background", activity.GetTagItem("operation.type"));
        Assert.Null(activity.GetTagItem("run.trigger"));
        Assert.Equal("processing", activity.GetTagItem("run.phase"));
        activities.AssertNoStatistics();
        Assert.Equal(outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error, activity.Status);
        Assert.Same(activities.Root, Activity.Current);

        if (outcome == "cancellation")
        {
            Assert.Equal(true, activity.GetTagItem("run.cancelled"));
        }
    }

    [Fact]
    public async Task QueuedAutomaticWorkRetainsParentAfterPollingRunCompletes()
    {
        using var activities = new RecordedActivities();
        var continueWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.CompletedTask;

        await MihuBotAIActivitySource.RunAutomaticAsync("AutomaticTestPoll", _ =>
        {
            worker = Task.Run(async () =>
            {
                await continueWorker.Task;
                using var issueActivity = MihuBotAIActivitySource.Instance.StartActivity("AutomaticTestIssue");
                Assert.NotNull(issueActivity);
                issueActivity.SetSuccess();
            });
            return Task.CompletedTask;
        });

        var poll = Assert.Single(activities.Stopped);
        Assert.Equal(ActivityStatusCode.Ok, poll.Status);
        continueWorker.SetResult();
        await worker;

        var issue = Assert.Single(activities.Stopped, a => a.OperationName == "AutomaticTestIssue");
        Assert.Equal(poll.TraceId, issue.TraceId);
        Assert.Equal(poll.SpanId, issue.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Ok, issue.Status);
        Assert.Same(activities.Root, Activity.Current);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task LabelResponseRecordsOutcomeWithoutPromptOrResponseContent(string outcome)
    {
        using var activities = new RecordedActivities();
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("Model unavailable");
        using var chat = new TestChatClient(() => outcome == "failure"
            ? Task.FromException<ChatResponse>(failure)
            : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"data":[{"labelName":"area-Test","confidence":0.9}]}"""))));

        if (outcome == "cancellation")
        {
            cancellation.Cancel();
        }

        const string Prompt = "Private prompt content";
        var responseTask = AreaLabelDetector.GetPredictionResponseAsync(chat, Prompt, new(), cancellation.Token);

        if (outcome == "failure")
        {
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => responseTask));
        }
        else if (outcome == "cancellation")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
        }
        else
        {
            Assert.Equal("area-Test", Assert.Single((await responseTask).Result!).LabelName);
        }

        var activity = Assert.Single(activities.Stopped);
        Assert.Equal("PredictLabelResponse", activity.OperationName);
        Assert.Equal(activities.Root.SpanId, activity.ParentSpanId);
        Assert.Equal("triage", activity.GetTagItem("operation.type"));
        Assert.Equal(Prompt.Length, activity.GetTagItem("prompt.length"));
        Assert.Equal(outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Error, activity.Status);
        Assert.Equal(outcome == "cancellation" ? 0 : 1, chat.Requests);
        Assert.DoesNotContain(activity.TagObjects, t => Equals(t.Value, Prompt));

        if (outcome == "success")
        {
            var responseEvent = Assert.Single(activity.Events);
            Assert.Equal("AiResponded", responseEvent.Name);
            Assert.DoesNotContain(responseEvent.Tags, t => t.Key == "response.full");
        }
        else
        {
            Assert.NotEmpty(activity.StatusDescription!);
        }
    }

    [Fact]
    public async Task CachedPredictionRecordsResultWithoutModelCall()
    {
        using var activities = new RecordedActivities();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        AreaLabelSuggestion[] cached = [new("area-Test", 0.9)];
        await cache.SetAsync(AreaLabelDetectionTests.GetPredictionCacheKey(), cached);
        using var detector = AreaLabelDetectionTests.CreateCachedDetector(cache);

        Assert.Equal(cached, await detector.PredictAsync("dotnet/runtime", 123, "area-", CancellationToken.None));

        var activity = Assert.Single(activities.Stopped);
        Assert.Equal("PredictIssueLabels", activity.OperationName);
        Assert.Equal(activities.Root.SpanId, activity.ParentSpanId);
        Assert.Equal("dotnet/runtime", activity.GetTagItem("issue.repository"));
        Assert.Equal(123, activity.GetTagItem("issue.number"));
        Assert.Equal(1, activity.GetTagItem("results.count"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    [Fact]
    public async Task SimilarIssueSearchRecordsEmptyResultWithTracingEnabled()
    {
        using var activities = new RecordedActivities();
        int searches = 0;

        var results = await AreaLabelDetector.FindSimilarIssuesAsync(new() { Id = "target" }, ["area-Test"],
            (_, _) =>
            {
                searches++;
                return Task.FromResult(GitHubSearchResponse.Empty);
            }, [], CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(3, searches);
        var activity = Assert.Single(activities.Stopped);
        Assert.Equal("FindSimilarIssuesForLabels", activity.OperationName);
        Assert.Equal(activities.Root.SpanId, activity.ParentSpanId);
        Assert.Equal(0, activity.GetTagItem("results.count"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    internal sealed class RecordedActivities : IDisposable
    {
        public Activity Root { get; } = new Activity("AiSearchActivityTest").Start();
        public ConcurrentQueue<Activity> Stopped { get; } = new();
        private readonly ActivityListener _listener;

        public RecordedActivities()
        {
            string sourceName = MihuBotAIActivitySource.Instance.Name;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Parent.TraceId == Root.TraceId ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId == Root.TraceId)
                    {
                        Stopped.Enqueue(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            Root.Dispose();
        }

        public void AssertNoStatistics()
        {
            Assert.All(Stopped, activity => Assert.DoesNotContain(activity.TagObjects, tag =>
                tag.Key.StartsWith("items.", StringComparison.Ordinal) ||
                tag.Key.StartsWith("timings.", StringComparison.Ordinal) ||
                tag.Key.EndsWith(".count", StringComparison.Ordinal) ||
                tag.Key.EndsWith(".length", StringComparison.Ordinal) ||
                tag.Key.EndsWith(".characters", StringComparison.Ordinal) ||
                tag.Key is "graphql.calls" or "graphql.cost" or "files.fetched" or "files.total" or "patches.incomplete" or
                    "references.stored" or "references.remote" or "references.remote.results" or "references.stored.privateExcluded" or
                    "copilot.tasks.listed" or "copilot.tasks.selected" or "copilot.tasks.failed" or "tokens.budget"));
        }
    }

    private sealed class TestChatClient(Func<Task<ChatResponse>> respond) : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++;
            return respond();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
