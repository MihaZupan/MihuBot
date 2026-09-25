using System.Collections.Concurrent;
using System.Diagnostics;
using Discord;
using MihuBot.Discord;
using MihuBot.RuntimeUtils;

namespace MihuBot.Tests.Discord;

[Collection(nameof(DiscordCommandActivityTests))]
public sealed class DiscordCommandActivityTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("cancelledReturn")]
    [InlineData("unrelatedCancellation")]
    [InlineData("handledFailure")]
    public async Task RecordsOutcomeAndPreservesBehavior(string outcome)
    {
        using var activities = new RecordedActivities();
        using var cancellation = new CancellationTokenSource();
        Exception failure = outcome is "cancelled" or "unrelatedCancellation"
            ? new OperationCanceledException("Private cancellation text", cancellation.Token)
            : new InvalidOperationException("Private command arguments");

        Task run = MihuBotDiscordActivitySource.RunAsync("ExecuteDiscordCommand", "test",
            1, 2, ulong.MaxValue, 4, async () =>
            {
                var activity = Activity.Current;
                Assert.NotNull(activity);
                Assert.NotSame(activities.Root, activity);
                await Task.Yield();
                Assert.Same(activity, Activity.Current);

                if (outcome is "cancelled" or "cancelledReturn")
                {
                    cancellation.Cancel();
                }

                if (outcome is "failed" or "cancelled" or "unrelatedCancellation")
                {
                    throw failure;
                }

                if (outcome == "handledFailure")
                {
                    activity.SetStatus(ActivityStatusCode.Error, "Handled failure");
                }
            }, cancellationToken: cancellation.Token);

        if (outcome is "failed" or "cancelled" or "unrelatedCancellation")
        {
            Assert.Same(failure, await Record.ExceptionAsync(() => run));
        }
        else
        {
            await run;
        }

        var activity = Assert.Single(activities.Stopped);
        Assert.Equal("ExecuteDiscordCommand", activity.OperationName);
        Assert.Equal(activities.Root.SpanId, activity.ParentSpanId);
        Assert.Equal("test", activity.GetTagItem("command.name"));
        Assert.Equal("1", activity.GetTagItem("discord.guild.id"));
        Assert.Equal("2", activity.GetTagItem("discord.channel.id"));
        Assert.Equal("18446744073709551615", activity.GetTagItem("discord.message.id"));
        Assert.Equal("4", activity.GetTagItem("discord.user.id"));
        Assert.Null(activity.GetTagItem("discord.interaction.id"));
        Assert.Null(activity.GetTagItem("discord.component.type"));
        Assert.Same(activities.Root, Activity.Current);

        string? expectedOutcome = outcome switch
        {
            "cancelledReturn" => "cancelled",
            "unrelatedCancellation" => "failed",
            "handledFailure" => null,
            _ => outcome
        };
        ActivityStatusCode expectedStatus = outcome switch
        {
            "failed" or "unrelatedCancellation" or "handledFailure" => ActivityStatusCode.Error,
            "cancelled" or "cancelledReturn" => ActivityStatusCode.Unset,
            _ => ActivityStatusCode.Ok
        };

        Assert.Equal(expectedOutcome, activity.GetTagItem("command.outcome"));
        Assert.Equal(expectedStatus, activity.Status);

        if (expectedOutcome == "failed")
        {
            Assert.Equal(failure.GetType().FullName, activity.GetTagItem("error.type"));
            Assert.Equal(failure.GetType().Name, activity.StatusDescription);
        }

        Assert.DoesNotContain("Private", activity.StatusDescription ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains("Private", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key.EndsWith(".count", StringComparison.Ordinal));
        Assert.Empty(activity.Events);
    }

    [Fact]
    public async Task BackgroundExecutionParentsAiWorkAndRecordsAlias()
    {
        using var activities = new RecordedActivities();

        await Task.Run(async () =>
        {
            await MihuBotDiscordActivitySource.RunAsync("ExecuteDiscordCommand", "test", 1, 2, 3, 4, async () =>
            {
                using var ai = MihuBotAIActivitySource.Instance.StartActivity("DiscordCommandAiWork");
                Assert.NotNull(ai);
                await Task.Yield();
            }, invokedAs: "alias");
        });

        var execution = Assert.Single(activities.Stopped, a => a.OperationName == "ExecuteDiscordCommand");
        var ai = Assert.Single(activities.Stopped, a => a.OperationName == "DiscordCommandAiWork");
        Assert.Equal(2, activities.Stopped.Count);
        Assert.Equal("alias", execution.GetTagItem("command.invoked_as"));
        Assert.Equal("completed", execution.GetTagItem("command.outcome"));
        Assert.Equal(activities.Root.SpanId, execution.ParentSpanId);
        Assert.Equal(execution.SpanId, ai.ParentSpanId);
        Assert.All(activities.Stopped, a => Assert.Equal(activities.Root.TraceId, a.TraceId));
        Assert.Same(activities.Root, Activity.Current);
    }

    [Theory]
    [InlineData(ComponentType.Button)]
    [InlineData(ComponentType.SelectMenu)]
    public async Task ComponentOutsideGuildRecordsMetadataAndOmitsGuildTag(ComponentType componentType)
    {
        using var activities = new RecordedActivities();

        await MihuBotDiscordActivitySource.RunAsync("HandleDiscordMessageComponent", "test",
            null, 2, 3, 4, () => Task.CompletedTask,
            interactionId: ulong.MaxValue, componentType: componentType);

        var activity = Assert.Single(activities.Stopped);
        Assert.Equal("HandleDiscordMessageComponent", activity.OperationName);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key == "discord.guild.id");
        Assert.Null(activity.GetTagItem("command.invoked_as"));
        Assert.Equal("18446744073709551615", activity.GetTagItem("discord.interaction.id"));
        Assert.Equal(componentType.ToString(), activity.GetTagItem("discord.component.type"));
        Assert.Equal("completed", activity.GetTagItem("command.outcome"));
    }

    [Fact]
    public async Task UnsampledWorkStillExecutesAndPropagatesFailures()
    {
        using var activities = new RecordedActivities(sample: false);
        bool executed = false;
        var failure = new InvalidOperationException("Failure without tracing");

        await MihuBotDiscordActivitySource.RunAsync("ExecuteDiscordCommand", "test", 1, 2, 3, 4, async () =>
        {
            Assert.Same(activities.Root, Activity.Current);
            await Task.Yield();
            executed = true;
        });

        Assert.True(executed);
        Assert.Same(failure, await Record.ExceptionAsync(() =>
            MihuBotDiscordActivitySource.RunAsync("ExecuteDiscordCommand", "test", 1, 2, 3, 4, () =>
            {
                Assert.Same(activities.Root, Activity.Current);
                throw failure;
            })));
        Assert.Empty(activities.Stopped);
        Assert.Same(activities.Root, Activity.Current);
    }

    private sealed class RecordedActivities : IDisposable
    {
        private readonly ActivityListener _listener;
        public Activity Root { get; } = new Activity("DiscordCommandActivityTests").Start();
        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public RecordedActivities(bool sample = true)
        {
            string discordSource = MihuBotDiscordActivitySource.Instance.Name;
            string aiSource = MihuBotAIActivitySource.Instance.Name;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == discordSource || source.Name == aiSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    sample && options.TraceId == Root.TraceId
                        ? ActivitySamplingResult.AllDataAndRecorded
                        : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (sample && activity.TraceId == Root.TraceId)
                    {
                        Stopped.Enqueue(activity);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose()
        {
            _listener.Dispose();
            Root.Dispose();
        }
    }
}

[CollectionDefinition(nameof(DiscordCommandActivityTests), DisableParallelization = true)]
public sealed class DiscordCommandActivityCollection;
