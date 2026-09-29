using GitHub.Copilot;
using MihuBot.Agents;
using MihuBot.Helpers.AI;
using MihuBot.Tests.Configuration;

namespace MihuBot.Tests.Agents;

public sealed class AgentTests
{
    [Fact]
    public void RunTimeoutIsThirtyMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), CopilotAgentService.RunTimeout);
    }

    [Theory]
    [InlineData(null, "gpt-6.1-sol")]
    [InlineData("", "gpt-6.1-sol")]
    [InlineData("   ", "gpt-6.1-sol")]
    [InlineData("custom-model", "custom-model")]
    public void AgentModelDefaultsToSolAndRespectsOverrides(string? configured, string expected)
    {
        var configuration = new TestConfigurationService();

        if (configured is not null)
        {
            configuration.Set(null, "Agent.Model", configured);
        }

        Assert.Equal("gpt-6.1-sol", OpenAIService.DefaultAgentModel);
        Assert.Equal(expected, CopilotAgentService.GetModel(configuration));
    }

    [Theory]
    [InlineData("agent hello", true, "hello")]
    [InlineData("AgEnT hello", true, "hello")]
    [InlineData("AgEnT\nhello", false, null)]
    [InlineData("agent\t hello  ", false, null)]
    [InlineData("agent", false, null)]
    [InlineData("agent ", true, "")]
    [InlineData("agents hello", false, null)]
    [InlineData("agentic", false, null)]
    [InlineData("agency", false, null)]
    [InlineData("hello agent", false, null)]
    public void PlainCommandRequiresAWordBoundary(string content, bool expected, string? prompt)
    {
        Assert.Equal(expected, AgentCommand.TryGetPrompt(content, out string actual));
        Assert.Equal(prompt, actual);
    }

    [Fact]
    public void WorkspaceRootCannotOverlapBotDirectories()
    {
        using var files = new TestFiles();
        string bot = Path.Combine(files.Directory, "bot");

        Assert.Throws<ArgumentException>(() => new AgentWorkspaceStore(bot, bot));
        Assert.Throws<ArgumentException>(() => new AgentWorkspaceStore(Path.Combine(bot, "agents"), bot));
        Assert.Throws<ArgumentException>(() => new AgentWorkspaceStore(files.Directory, bot));
        Assert.Throws<ArgumentException>(() => new AgentWorkspaceStore("relative-path", bot));

        using var store = new AgentWorkspaceStore(bot + "-agents", bot);
    }

    [Fact]
    public void CleanupOnlyRemovesOwnedRunDirectories()
    {
        using var files = new TestFiles();
        string oldRun;
        string preserved = Path.Combine(files.Root, "not-a-run");

        using (var store = new AgentWorkspaceStore(files.Root))
        {
            oldRun = store.Create();
            string output = Path.Combine(oldRun, "output.txt");
            File.WriteAllText(output, "old output");
            File.SetAttributes(output, FileAttributes.ReadOnly);
            Directory.CreateDirectory(preserved);
            Directory.CreateDirectory(Path.Combine(files.Root, "run-not-a-guid"));

            Assert.Throws<ArgumentException>(() => store.Delete(files.Root));
            Assert.Throws<ArgumentException>(() => store.Delete(preserved));
            Assert.Throws<ArgumentException>(() => store.Delete(Path.Combine(files.Directory, $"run-{Guid.NewGuid():N}")));
            Assert.Throws<IOException>(() => new AgentWorkspaceStore(files.Root));
        }

        using var reopened = new AgentWorkspaceStore(files.Root);
        Assert.False(Directory.Exists(oldRun));
        Assert.True(Directory.Exists(preserved));
        Assert.True(Directory.Exists(Path.Combine(files.Root, "run-not-a-guid")));

        string first = reopened.Create();
        string second = reopened.Create();
        Assert.NotEqual(first, second);
        reopened.Delete(first);
        Assert.True(Directory.Exists(second));
        reopened.Delete(second);
    }

    [UnixFact]
    public void CleanupDoesNotFollowDirectoryLinks()
    {
        using var files = new TestFiles();
        using var store = new AgentWorkspaceStore(files.Root);
        string target = Path.Combine(files.Directory, "keep");
        Directory.CreateDirectory(target);
        string keep = Path.Combine(target, "important.txt");
        File.WriteAllText(keep, "keep");
        string run = store.Create();
        Directory.CreateSymbolicLink(Path.Combine(run, "linked"), target);

        store.Delete(run);
        Assert.True(File.Exists(keep));
        Directory.CreateSymbolicLink(run, target);
        store.Delete(run);
        Assert.True(File.Exists(keep));
        Directory.CreateSymbolicLink(Path.Combine(files.Directory, "root-link"), target);
        Assert.Throws<IOException>(() => new AgentWorkspaceStore(Path.Combine(files.Directory, "root-link", "agents")));
    }

    [Fact]
    public void ClientUsesFreshHomeAndAnExplicitEnvironment()
    {
        using var files = new TestFiles();
        using var store = new AgentWorkspaceStore(files.Root);
        string run = store.Create();
        string work = Path.Combine(run, "work");
        CopilotClientOptions options = CopilotAgentService.CreateClientOptions(run, work, "test-token");
        var connection = Assert.IsType<StdioRuntimeConnection>(options.Connection);

        Assert.Equal(work, options.WorkingDirectory);
        Assert.Equal(Path.Combine(run, "copilot"), options.BaseDirectory);
        Assert.Equal("test-token", options.GitHubToken);
        Assert.False(options.UseLoggedInUser);
        Assert.False(options.EnableRemoteSessions);
        Assert.NotNull(connection.Environment);
        Assert.Equal(Path.Combine(run, "home"), connection.Environment["HOME"]);
        Assert.Equal(Path.Combine(run, "tmp"), connection.Environment["TMPDIR"]);
        Assert.Equal("1", connection.Environment["COPILOT_DISABLE_KEYTAR"]);
        Assert.DoesNotContain("GITHUB_TOKEN", connection.Environment.Keys);
        Assert.DoesNotContain("COPILOT_GITHUB_TOKEN", connection.Environment.Keys);
        Assert.DoesNotContain("Azure__ClientSecret", connection.Environment.Keys);

        store.Delete(run);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    [InlineData("deadline")]
    public async Task BundledRuntimeAndWorkspaceAreCleanedUp(string outcome)
    {
        using var files = new TestFiles();
        using var store = new AgentWorkspaceStore(files.Root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CopilotClient? observedClient = null;
        string? observedWork = null;

        Task<string> task = CopilotAgentService.RunWithClientAsync(store, "", async (client, work, token) =>
        {
            observedClient = client;
            observedWork = work;
            var ping = await client.PingAsync("agent-smoke", token);
            Assert.Equal("pong: agent-smoke", ping.Message);
            File.WriteAllText(Path.Combine(work, "result.txt"), "output");

            if (outcome == "failure")
            {
                throw new InvalidDataException("Expected task failure.");
            }

            if (outcome == "cancellation")
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            if (outcome == "deadline")
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return "done";
        }, cancellation.Token);

        if (outcome == "success")
        {
            Assert.Equal("done", await task);
        }
        else if (outcome == "failure")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => task);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.NotNull(observedClient);
        Assert.NotNull(observedWork);
        Assert.False(Directory.Exists(observedWork));
        Assert.Empty(Directory.EnumerateDirectories(files.Root, "run-*"));
        Assert.Throws<ObjectDisposedException>(() => observedClient.Rpc);
    }

    [Fact]
    public async Task AlreadyCancelledRunDoesNotCreateAWorkspaceOrStartTheRuntime()
    {
        using var files = new TestFiles();
        using var store = new AgentWorkspaceStore(files.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CopilotAgentService.RunWithClientAsync(store, "", (_, _, _) =>
                throw new InvalidOperationException("The runtime must not start."), cancellation.Token));

        Assert.Empty(Directory.EnumerateDirectories(files.Root));
    }

    private sealed class TestFiles : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("mihubot-agent-tests-").FullName;
        public string Root => Path.Combine(Directory, "agents");

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "Creating symbolic links on Windows requires additional privileges.";
            }
        }
    }
}
