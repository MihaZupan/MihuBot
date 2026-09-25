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

    private sealed class RecordedActivities : IDisposable
    {
        public Activity Root { get; } = new Activity("AiSearchActivityTest").Start();
        public List<Activity> Stopped { get; } = [];
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
                        Stopped.Add(activity);
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
