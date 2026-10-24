using System.Diagnostics;
using System.Text.Json;
using Discord.WebSocket;
using Microsoft.Extensions.AI;
using MihuBot.Discord.Audio;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;

namespace MihuBot.Tests.Discord;

public sealed class SpokenAudioToolsTests
{
    [Fact]
    public async Task MultipleActionsCanChangePlaybackQueueSongsAndEditTheirOrder()
    {
        await using var player = new TestPlayer();
        var current = new Source("Current");
        var first = new Source("First");
        var second = new Source("Second");
        await player.StartAsync(current, first, second);
        IVideo[] videos = [Video("New song one"), Video("New song two")];
        using var chat = new TestChatClient(
            Call("SetVolume", new { percent = 30, relative = false }),
            Call("SetVolume", new { percent = -10, relative = true }),
            Call("Search", new { query = "requested songs" }),
            Call("QueueVideos", new { candidateIndices = new[] { 1, 0 } }),
            Call("MoveTrack", new { trackId = 4, beforeTrackId = 1 }),
            Call("RemoveTrack", new { trackId = 2 }),
            Call("SetPaused", new { paused = true }),
            new SpokenAudioToolCompletion(""));
        List<string> logs = [];
        var tools = new SpokenAudioTools(player, (query, _) =>
        {
            Assert.Equal("requested songs", query);
            return Task.FromResult(videos);
        });

        SpokenAudioToolResult result = await tools.RunAsync(chat, "MihuBot lower the volume and change the queue", _ => { }, logs.Add, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Empty(result.Error);
        SpokenPlaybackState state = player.GetState();
        Assert.Equal(0.2f, state.Volume, precision: 5);
        Assert.True(state.Paused);
        Assert.Same(current, state.Current);
        Assert.Equal(["New song one", "First", "New song two"], state.Queue.Select(source => source.Description));
        Assert.Equal([videos[1], videos[0]], player.QueuedVideos);
        Assert.Equal(1, second.DisposeCalls);
        Assert.Equal(0, current.DisposeCalls);
        Assert.Equal(7, logs.Count);
        Assert.Contains("SetVolume", logs[0], StringComparison.Ordinal);
        Assert.Contains("QueueVideos", logs[3], StringComparison.Ordinal);
        Assert.Equal(8, chat.Requests);
        using var initial = JsonDocument.Parse(chat.History[0].Single(message => message.Role == ChatRole.User).Text);
        JsonElement playback = initial.RootElement.GetProperty("Playback");
        Assert.Equal("Current", playback.GetProperty("Current").GetProperty("Title").GetString());
        Assert.Equal(0, playback.GetProperty("Current").GetProperty("Id").GetInt32());
        Assert.Equal(["First", "Second"], playback.GetProperty("Queue").EnumerateArray()
            .Select(track => track.GetProperty("Title").GetString()));
        Assert.Equal(40, playback.GetProperty("VolumePercent").GetInt32());
        Assert.False(playback.GetProperty("Paused").GetBoolean());
    }

    [Fact]
    public async Task ClearQueuePreservesCurrentTrackAndDisposesUpcomingTracks()
    {
        await using var player = new TestPlayer();
        var current = new Source("Current");
        var upcoming = new Source("Upcoming");
        await player.StartAsync(current, upcoming);
        using var chat = new TestChatClient(Call("ClearQueue", new { }), new SpokenAudioToolCompletion(""));

        await Tools(player).RunAsync(chat, "MihuBot clear the queue", _ => { }, _ => { }, CancellationToken.None);

        Assert.Same(current, player.GetState().Current);
        Assert.Empty(player.GetState().Queue);
        Assert.Equal(0, current.DisposeCalls);
        Assert.Equal(1, upcoming.DisposeCalls);
    }

    [Fact]
    public async Task MultipleActionsInOneRoundtripExecuteSequentiallyInModelOrder()
    {
        await using var player = new TestPlayer();
        using var chat = new TestChatClient(
            new[]
            {
                Call("SetVolume", new { percent = 30, relative = false }),
                Call("SetVolume", new { percent = -10, relative = true }),
                Call("SetPaused", new { paused = true })
            },
            new SpokenAudioToolCompletion(""));

        SpokenAudioToolResult result = await Tools(player).RunAsync(chat, "MihuBot change volume and pause", _ => { }, _ => { }, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(0.2f, player.GetState().Volume, precision: 5);
        Assert.True(player.GetState().Paused);
        Assert.Equal([SpokenAudioAction.Volume, SpokenAudioAction.Volume, SpokenAudioAction.Pause], player.Applied.Select(command => command.Action));
        Assert.Equal(2, chat.Requests);
    }

    [Fact]
    public async Task SkipUntilUsesStableTrackIdentityAndPreservesTheDestination()
    {
        await using var player = new TestPlayer();
        var first = new Source("First");
        var second = new Source("Second");
        var destination = new Source("Destination");
        await player.StartAsync(first, second, destination);
        using var chat = new TestChatClient(Call("SkipUntil", new { trackId = 2 }), new SpokenAudioToolCompletion(""));

        await Tools(player).RunAsync(chat, "MihuBot skip until Destination", _ => { }, _ => { }, CancellationToken.None);

        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        Assert.Equal(0, destination.DisposeCalls);
        Assert.Same(destination, await player.Scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CountedSkipAndResumeCanBeCombined()
    {
        await using var player = new TestPlayer();
        await player.StartAsync(new Source("First"), new Source("Second"), new Source("Third"));
        using var chat = new TestChatClient(
            Call("Skip", new { count = 2 }),
            Call("SetPaused", new { paused = false }),
            new SpokenAudioToolCompletion(""));

        await Tools(player).RunAsync(chat, "MihuBot skip two tracks and resume", _ => { }, _ => { }, CancellationToken.None);

        Assert.Equal("Third", Assert.Single(player.GetState().Queue).Description);
        Assert.False(player.GetState().Paused);
        Assert.Equal(2, Assert.Single(player.Applied, command => command.Action == SpokenAudioAction.Skip).Count);
    }

    [Theory]
    [InlineData("RemoveTrack", 0)]
    [InlineData("RemoveTrack", 999)]
    [InlineData("MoveTrack", 0)]
    [InlineData("MoveTrack", 999)]
    public async Task CurrentOrInventedTracksCannotBeRemovedOrMoved(string tool, int trackId)
    {
        await using var player = new TestPlayer();
        await player.StartAsync(new Source("Current"), new Source("Upcoming"));
        using var chat = new TestChatClient(Call(tool, new { trackId, beforeTrackId = (int?)null }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tools(player).RunAsync(chat, "MihuBot edit the queue", _ => { }, _ => { }, CancellationToken.None));

        Assert.Equal("Current", player.GetState().Current!.Description);
        Assert.Equal("Upcoming", Assert.Single(player.GetState().Queue).Description);
    }

    [Fact]
    public async Task QueueVideosCannotUseAnInventedSearchCandidate()
    {
        await using var player = new TestPlayer();
        using var chat = new TestChatClient(Call("QueueVideos", new { candidateIndices = new[] { 0 } }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tools(player).RunAsync(chat, "MihuBot queue a song", _ => { }, _ => { }, CancellationToken.None));

        Assert.Empty(player.QueuedVideos);
    }

    [Fact]
    public async Task FailureStopsFurtherToolsWithoutRevertingEarlierChanges()
    {
        await using var player = new TestPlayer();
        using var chat = new TestChatClient(
            Call("SetVolume", new { percent = 25, relative = false }),
            Call("QueueVideos", new { candidateIndices = new[] { 999 } }),
            Call("SetVolume", new { percent = 100, relative = false }));
        List<string> logs = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tools(player).RunAsync(chat, "MihuBot change volume and queue songs", _ => { }, logs.Add, CancellationToken.None));

        Assert.Equal(0.25f, player.GetState().Volume);
        Assert.Single(player.Applied);
        Assert.Single(logs);
        Assert.Equal(2, chat.Requests);
    }

    [Fact]
    public async Task BatchedActionsStopAtTheFirstFailureWithoutRetryingOrApplyingLaterChanges()
    {
        await using var player = new TestPlayer();
        using var chat = new TestChatClient((object)new[]
        {
            Call("SetVolume", new { percent = 25, relative = false }),
            Call("QueueVideos", new { candidateIndices = new[] { 999 } }),
            Call("SetVolume", new { percent = 100, relative = false })
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tools(player).RunAsync(chat, "MihuBot change volume and queue songs", _ => { }, _ => { }, CancellationToken.None));

        Assert.Equal(0.25f, player.GetState().Volume);
        Assert.Single(player.Applied);
        Assert.Equal(1, chat.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopIsTerminalAndDoesNotReadPlaybackOrCallTheModelAgain(bool cancelDuringStop)
    {
        using var cancellation = new CancellationTokenSource();
        await using var player = new TestPlayer
        {
            OnStop = () =>
            {
                if (cancelDuringStop)
                {
                    cancellation.Cancel();
                }
            }
        };
        using var chat = new TestChatClient((object)new[]
        {
            Call("Stop", new { }),
            Call("SetVolume", new { percent = 100, relative = false })
        });

        SpokenAudioToolResult result = await Tools(player).RunAsync(chat, "MihuBot stop", _ => { }, _ => { }, cancellation.Token);

        Assert.True(result.Changed);
        Assert.True(player.Stopped);
        Assert.Equal(1, chat.Requests);
        Assert.Equal(SpokenAudioAction.Stop, Assert.Single(player.Applied).Action);
    }

    [Fact]
    public async Task UnclearRequestCanReturnAnErrorWithoutApplyingChanges()
    {
        await using var player = new TestPlayer();
        using var chat = new TestChatClient(new SpokenAudioToolCompletion("Please name the songs you want."));

        SpokenAudioToolResult result = await Tools(player).RunAsync(chat, "MihuBot queue those songs", _ => { }, _ => { }, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal("Please name the songs you want.", result.Error);
        Assert.Empty(player.Applied);
        Assert.Empty(player.QueuedVideos);
    }

    [Fact]
    public async Task CancellationAfterOneActionPreventsFurtherChanges()
    {
        await using var player = new TestPlayer();
        using var cancellation = new CancellationTokenSource();
        using var chat = new TestChatClient(
            Call("SetVolume", new { percent = 25, relative = false }),
            Call("SetVolume", new { percent = 100, relative = false }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tools(player).RunAsync(chat, "MihuBot change volume",
            _ => { }, _ => cancellation.Cancel(), cancellation.Token));

        Assert.Equal(0.25f, player.GetState().Volume);
        Assert.Single(player.Applied);
        Assert.Equal(1, chat.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolBudgetAllowsItsFullLimitAndRejectsFurtherMutations(bool exceedLimit, bool batched)
    {
        await using var player = new TestPlayer();
        FunctionCallContent[] calls = Enumerable.Range(0, SpokenAudioTools.MaxToolCalls + (exceedLimit ? 1 : 0))
            .Select(_ => Call("SetVolume", new { percent = 1, relative = true })).ToArray();
        using var chat = new TestChatClient(batched
            ? [calls, new SpokenAudioToolCompletion("")]
            : [.. calls, new SpokenAudioToolCompletion("")]);

        if (exceedLimit)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Tools(player).RunAsync(chat, "MihuBot louder", _ => { }, _ => { }, CancellationToken.None));
        }
        else
        {
            Assert.True((await Tools(player).RunAsync(chat, "MihuBot louder", _ => { }, _ => { }, CancellationToken.None)).Changed);
        }

        Assert.Equal(SpokenAudioTools.MaxToolCalls, player.Applied.Count);
        Assert.Equal(batched ? (exceedLimit ? 1 : 2) : SpokenAudioTools.MaxToolCalls + 1, chat.Requests);
        Assert.Equal(batched && exceedLimit, chat.LastHadTools);
    }

    private static SpokenAudioTools Tools(TestPlayer player) =>
        new(player, (_, _) => throw new InvalidOperationException("Search should not run."));

    private static FunctionCallContent Call(string name, object arguments) =>
        new(Guid.NewGuid().ToString("N"), name, JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

    private static IVideo Video(string title) =>
        new Video("afqQwmXTcPA", title, new Author("UC0123456789012345678901", "Artist"), DateTimeOffset.UtcNow,
            "", TimeSpan.FromMinutes(4), [], [], new Engagement(0, 0, 0));

    private sealed class TestChatClient(params object[] responses) : IChatClient
    {
        private readonly Queue<object> _responses = new(responses);
        public int Requests { get; private set; }
        public bool LastHadTools { get; private set; }
        public List<ChatMessage[]> History { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            History.Add(messages.ToArray());
            LastHadTools = options?.Tools is { Count: > 0 } && options.ToolMode != ChatToolMode.None;
            object response = _responses.Dequeue();
            return Task.FromResult(new ChatResponse(response switch
            {
                FunctionCallContent call => new ChatMessage(ChatRole.Assistant, [call]),
                FunctionCallContent[] calls => new ChatMessage(ChatRole.Assistant, [.. calls]),
                _ => new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(response))
            }));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestPlayer : ISpokenAudioPlayer, IAsyncDisposable
    {
        public AudioScheduler Scheduler { get; } = new();
        public List<SpokenAudioCommand> Applied { get; } = [];
        public List<IVideo> QueuedVideos { get; } = [];
        public bool Stopped { get; private set; }
        public Action? OnStop { get; init; }
        private float _volume = 0.4f;
        private bool _paused;

        public async Task StartAsync(params Source[] sources)
        {
            foreach (Source source in sources)
            {
                Scheduler.Enqueue(source);
            }

            await Scheduler.GetAudioSourceAsync(CancellationToken.None);
        }

        public SpokenPlaybackState GetState()
        {
            Assert.False(Stopped, "Playback must not be inspected after Stop.");
            var (current, queue) = Scheduler.GetEditableQueueSnapshot();
            return new(current, queue, _volume, _paused);
        }

        public async Task ApplyAsync(SpokenAudioCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Applied.Add(command);

            switch (command.Action)
            {
                case SpokenAudioAction.Volume:
                    _volume = command.IsRelativeVolume ? Math.Clamp(_volume + command.VolumePercent!.Value / 100f, 0, 1) : command.VolumePercent!.Value / 100f;
                    break;
                case SpokenAudioAction.Pause:
                case SpokenAudioAction.Resume:
                    _paused = command.Action == SpokenAudioAction.Pause;
                    break;
                case SpokenAudioAction.Skip:
                case SpokenAudioAction.SkipUntil:
                    AudioSkipBatch batch = Scheduler.SkipTracks(command.Count, Stopwatch.GetTimestamp(), command.TargetSource!);
                    await AudioScheduler.DisposeSourcesAsync(batch.ToDispose);
                    break;
                case SpokenAudioAction.Stop:
                    Stopped = true;
                    OnStop?.Invoke();
                    break;
                default:
                    throw new InvalidOperationException("Unexpected test action.");
            }
        }

        public Task QueueAsync(IVideo[] videos, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueuedVideos.AddRange(videos);

            foreach (IVideo video in videos)
            {
                Scheduler.Enqueue(new Source(video.Title));
            }

            return Task.CompletedTask;
        }

        public Task ReplaceQueueAsync(IAudioSource[] expected, IAudioSource[] replacement, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Scheduler.ReplaceQueueAsync(expected, replacement);
        }

        public ValueTask DisposeAsync() => Scheduler.DisposeAsync();
    }

    private sealed class Source(string title) : IAudioSource
    {
        public string Description => title;
        public string Url => "";
        public string ThumbnailUrl => "";
        public TimeSpan? Duration => TimeSpan.FromMinutes(4);
        public TimeSpan? Remaining => Duration;
        public SocketGuildUser Requester => null!;
        public bool IsAutoplay => false;
        public int DisposeCalls { get; private set; }
        public void StartInitializing(int bitrateHintKbit) { }
        public Task<bool> EnsureInitializedAsync() => Task.FromResult(true);
        public ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken) => ValueTask.FromResult(0);

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
