using GitHub.Copilot;
using Discord;
using MihuBot.Agents;
using System.Diagnostics;
using System.Text.Json;

namespace MihuBot.Tests.Agents;

public sealed class AgentProgressTests
{
    [Fact]
    public void RendersElapsedTimeToolActivityAndFailuresAcrossAgents()
    {
        var progress = new AgentProgress();
        Embed initial = progress.Render(TimeSpan.Zero);
        Assert.Equal("MihuBot Agent", initial.Title);
        Assert.Equal(new Color(88, 101, 242), initial.Color);
        Assert.Contains("Starting Copilot", initial.Description, StringComparison.Ordinal);

        progress.OnEvent(Start("1", "powershell", "Analyze data with Python"));
        progress.OnEvent(Start("1", "web_fetch", agentId: "child"));
        progress.OnEvent(Complete("1", false, "child"));
        progress.OnEvent(Complete("1", false, "child"));

        Embed embed = progress.Render(TimeSpan.FromSeconds(12));
        Assert.Equal("00:12", embed.Fields.Single(f => f.Name == "Elapsed").Value);
        EmbedField running = embed.Fields.Single(f => f.Name == "Running: powershell");
        Assert.Equal("Analyze data with Python", running.Value);
        Assert.False(running.Inline);
        string content = Text(embed);
        Assert.Contains("1 running, 1 finished, 1 failed", content, StringComparison.Ordinal);
        Assert.Contains("Failed: **web fetch**", content, StringComparison.Ordinal);
        Assert.Null(embed.Footer);
        Assert.DoesNotContain("React to", content, StringComparison.Ordinal);

        progress.OnEvent(Complete("1", true));
        embed = progress.Render(TimeSpan.FromSeconds(14));
        content = Text(embed);
        Assert.Contains("0 running, 2 finished, 1 failed", content, StringComparison.Ordinal);
        Assert.Contains("Done: **powershell** - Analyze data with Python", embed.Fields.Single(f => f.Name == "Recent activity").Value, StringComparison.Ordinal);
        Assert.DoesNotContain(embed.Fields, f => f.Name.StartsWith("Running:", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamsOnlyLatestRootMessageWithoutDuplicatingTheCompletedMessage()
    {
        var progress = new AgentProgress();
        progress.OnEvent(Delta("1", "Looking "));
        progress.OnEvent(Delta("1", "up sources."));
        Assert.Contains("Looking up sources.", Text(progress.Render(TimeSpan.Zero)), StringComparison.Ordinal);

        progress.OnEvent(new AssistantMessageEvent
        {
            Data = new AssistantMessageData
            {
                MessageId = "1",
                Content = "Looking up sources.",
                ReasoningText = "PRIVATE REASONING"
            }
        });
        progress.OnEvent(Delta("child-message", "CHILD MESSAGE", "child"));

        string content = Text(progress.Render(TimeSpan.Zero));
        Assert.EndsWith("Looking up sources.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Looking up sources.Looking", content, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE REASONING", content, StringComparison.Ordinal);
        Assert.DoesNotContain("CHILD MESSAGE", content, StringComparison.Ordinal);

        progress.OnEvent(Delta("2", "Here is the answer."));
        content = Text(progress.Render(TimeSpan.Zero));
        Assert.EndsWith("Here is the answer.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Looking up sources.", content, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsProgressBoundedAndDoesNotExposeToolArguments()
    {
        var progress = new AgentProgress();

        for (int i = 0; i < 10; i++)
        {
            progress.OnEvent(Start($"old-{i}", $"tool-{i}"));
            progress.OnEvent(Complete($"old-{i}", true));
        }

        for (int i = 0; i < 20; i++)
        {
            var evt = Start($"running-{i}", new string('x', 300), "`title`\n" + new string('y', 300));
            evt.Data.Arguments = JsonSerializer.SerializeToElement(new { command = "SECRET TOOL ARGUMENT" });
            progress.OnEvent(evt);
        }

        progress.OnEvent(Delta("1", new string('z', 5000)));
        progress.OnEvent(Delta("1", "latest text"));
        Embed embed = progress.Render(TimeSpan.FromMinutes(9));
        string content = Text(embed);

        Assert.True(content.Length <= 6000, $"Progress contained {content.Length} characters.");
        Assert.All(embed.Fields, field =>
        {
            Assert.InRange(field.Name.Length, 1, 256);
            Assert.InRange(field.Value.Length, 1, 1024);
        });
        Assert.Contains("20 running, 10 finished", content, StringComparison.Ordinal);
        Assert.Contains("16 more running", content, StringComparison.Ordinal);
        Assert.Contains("Done: **tool-4**", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Done: **tool-3**", content, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET TOOL ARGUMENT", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`title`\n", content, StringComparison.Ordinal);
        Assert.EndsWith("latest text", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoalescesEventsAndDrainsInFlightEditBeforeFinalResponse()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), AgentProgress.UpdateInterval);
        var progress = new AgentProgress();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = Stopwatch.StartNew();
        var edits = new List<string>();
        var errors = new List<Exception>();

        Task updater = progress.RunUpdatesAsync(async text =>
        {
            Assert.True(elapsed.Elapsed >= AgentProgress.UpdateInterval);
            entered.TrySetResult();
            await release.Task;
            edits.Add(Text(text));
        }, errors.Add, cancellation.Token);

        try
        {
            for (int i = 0; i < 100; i++)
            {
                progress.OnEvent(Delta($"message-{i}", $"Update {i}"));
            }

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            Assert.False(updater.IsCompleted);
            Assert.Empty(edits);
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            await updater.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(errors);
        Assert.EndsWith("Update 99", Assert.Single(edits), StringComparison.Ordinal);
        edits.Add("Final response");
        Assert.Equal("Final response", edits[^1]);
        Assert.Equal(2, edits.Count);
    }

    [Fact]
    public async Task ProgressFailureIsLoggedAndStopsUpdatesWithoutFailingTheAgent()
    {
        var progress = new AgentProgress();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var errors = new List<Exception>();
        var failure = new IOException("Discord message was deleted.");
        int edits = 0;

        await progress.RunUpdatesAsync(_ =>
        {
            edits++;
            throw failure;
        }, errors.Add, cancellation.Token);

        Assert.Equal(1, edits);
        Assert.Same(failure, Assert.Single(errors));
    }

    [Fact]
    public async Task CancelledUpdaterDoesNotPublish()
    {
        var progress = new AgentProgress();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var errors = new List<Exception>();

        await progress.RunUpdatesAsync(_ => throw new InvalidOperationException("Must not publish."), errors.Add, cancellation.Token);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("powershell", """{"description":"Calculate message counts with Python","command":"SECRET COMMAND"}""", "Calculate message counts with Python")]
    [InlineData("web_search", """{"query":"latest .NET release"}""", "Running command\n\nlatest .NET release")]
    [InlineData("web_fetch", """{"url":"https://example.com/docs"}""", "Running command\n\nhttps://example.com/docs")]
    [InlineData("upload_file", """{"path":"plots/activity.png"}""", "Running command\n\nplots/activity.png")]
    [InlineData("search_channel_messages", """{"text":"release announcement","authorId":"42"}""", "Running command\n\nrelease announcement")]
    [InlineData("search_channel_messages", """{"authorId":"42"}""", "Running command\n\n42")]
    public void ToolDescriptionsShowPurposeAndTargets(string name, string arguments, string expected)
    {
        var evt = Start("1", name, "Running command");
        evt.Data.Arguments = JsonSerializer.Deserialize<JsonElement>(arguments);
        Assert.Equal(expected, AgentProgress.DescribeTool(evt.Data).Details);
    }

    [Fact]
    public void StreamedMarkdownIsPreservedWithinEmbedFieldLimits()
    {
        var progress = new AgentProgress();
        progress.OnEvent(Delta("1", string.Concat(Enumerable.Repeat("https://a.co ", 200))));

        EmbedField[] fields = [.. progress.Render(TimeSpan.Zero).Fields.Where(f => f.Name.StartsWith("Latest update", StringComparison.Ordinal))];
        string update = string.Concat(fields.Select(f => f.Value));
        Assert.All(fields, field => Assert.InRange(field.Value.Length, 1, 1024));
        Assert.True(update.Length > 2000);
        Assert.Contains("https://a.co ", update, StringComparison.Ordinal);
        Assert.DoesNotContain("<https://a.co>", update, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumDetailsFitDiscordEmbedLimits()
    {
        var progress = new AgentProgress();

        for (int i = 0; i < 10; i++)
        {
            progress.OnEvent(Start($"done-{i}", new string('x', 500), new string('d', 500)));
            progress.OnEvent(Complete($"done-{i}", false));
            progress.OnEvent(Start($"running-{i}", new string('y', 500), new string('d', 500)));
        }

        progress.OnEvent(Delta("1", new string('z', 5000)));
        progress.OnEvent(AgentUsageTests.Event(new string('m', 500), 1_000_000, 100_000));
        Embed embed = progress.Render(TimeSpan.FromMinutes(10));

        Assert.InRange(embed.Fields.Length, 1, 25);
        Assert.All(embed.Fields, field => Assert.InRange(field.Value.Length, 1, 1024));
        int totalLength = embed.Title.Length + embed.Description.Length + embed.Fields.Sum(f => f.Name.Length + f.Value.Length) + (embed.Footer?.Text.Length ?? 0);
        Assert.InRange(totalLength, 1, 6000);
        Assert.Equal(2500, embed.Fields.Where(f => f.Name.StartsWith("Latest update", StringComparison.Ordinal)).Sum(f => f.Value.Length));
    }

    [Fact]
    public void LongSearchAndFetchDetailsStayInSeparateFullWidthFields()
    {
        var progress = new AgentProgress();
        var fetch = Start("fetch", "web_fetch", "Fetching web content");
        fetch.Data.Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com/cityneversleeps" });
        var search = Start("search", "web_search", "Web Search");
        string query = "Find the official City Never Sleeps announcement and summarize its features and release dates.";
        search.Data.Arguments = JsonSerializer.SerializeToElement(new { query });
        progress.OnEvent(fetch);
        progress.OnEvent(search);

        EmbedField[] fields = [.. progress.Render(TimeSpan.Zero).Fields.Where(f => f.Name.StartsWith("Running:", StringComparison.Ordinal))];
        Assert.Equal(2, fields.Length);
        Assert.All(fields, field =>
        {
            Assert.False(field.Inline);
            Assert.DoesNotContain('`', field.Value);
        });
        Assert.Equal("Fetching web content\n\nhttps://example.com/cityneversleeps", fields[0].Value);
        Assert.Equal($"Web Search\n\n{query}", fields[1].Value);
    }

    [Fact]
    public void CompletedToolsUseCompactRowsWithoutRepeatingGenericDescriptions()
    {
        var progress = new AgentProgress();
        var fetch = Start("fetch", "web_fetch", "Fetching web content");
        fetch.Data.Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com/docs" });
        progress.OnEvent(fetch);
        progress.OnEvent(Complete("fetch", true));
        progress.OnEvent(Start("history", "read_channel_history"));
        progress.OnEvent(Complete("history", false));

        Embed embed = progress.Render(TimeSpan.Zero);
        string recent = embed.Fields.Single(f => f.Name == "Recent activity").Value;
        Assert.Equal("- Done: **web fetch** - https://example.com/docs\n- Failed: **read channel history**", recent);
        Assert.DoesNotContain("Fetching web content", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", recent, StringComparison.Ordinal);
        Assert.DoesNotContain(embed.Fields, f => f.Name.StartsWith("Done:", StringComparison.Ordinal));
        Assert.All(recent.Split('\n'), line => Assert.InRange(line.Length, 1, 160));
    }

    private static ToolExecutionStartEvent Start(string id, string name, string? title = null, string? agentId = null) => new()
    {
        AgentId = agentId,
        Data = new ToolExecutionStartData { ToolCallId = id, ToolName = name, ToolTitle = title }
    };

    private static ToolExecutionCompleteEvent Complete(string id, bool success, string? agentId = null) => new()
    {
        AgentId = agentId,
        Data = new ToolExecutionCompleteData { ToolCallId = id, Success = success }
    };

    private static AssistantMessageDeltaEvent Delta(string id, string content, string? agentId = null) => new()
    {
        AgentId = agentId,
        Data = new AssistantMessageDeltaData { MessageId = id, DeltaContent = content }
    };

    private static string Text(Embed embed) => string.Join('\n',
        new[] { embed.Title, embed.Description, embed.Footer?.Text }.Concat(embed.Fields.Select(f => f.Value)));
}
