using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using MihuBot.DB;
using MihuBot.Discord.Audio;

namespace MihuBot.Tests.Discord;

public sealed class AudioConnectionTests
{
    [Fact]
    public async Task SameChannelAndUnrelatedVoiceUpdatesDoNotStopPlayback()
    {
        await using var fixture = new Connections();
        AudioPlayer player = await fixture.GetAsync();

        await fixture.Service.HandleVoiceChannelChangedAsync(0, 0, 0);
        await fixture.Service.HandleVoiceChannelChangedAsync(0, 99, null);

        Assert.True(fixture.Service.TryGetAudioPlayer(0, out AudioPlayer current));
        Assert.Same(player, current);
        Assert.Equal(0, fixture.Created[0].Transport.StopCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(42ul)]
    public async Task KicksAndMovesCleanUpAndAllowRejoining(ulong? destination)
    {
        await using var fixture = new Connections();
        AudioPlayer first = await fixture.GetAsync();

        await fixture.Service.HandleVoiceChannelChangedAsync(0, 0, destination);

        Connection old = fixture.Created[0];
        Assert.False(fixture.Service.TryGetAudioPlayer(0, out _));
        Assert.Equal(1, old.Transport.StopCalls);
        Assert.Equal(1, old.DisconnectCalls);
        Assert.True(old.Transport.Disposed);
        Assert.True(old.Transport.Output.Disposed);
        Assert.Null(old.Transport.Disconnected);
        Assert.NotSame(first, await fixture.GetAsync());
    }

    [Fact]
    public async Task ConcurrentRequestsWaitForOneConnectionToBecomeReady()
    {
        await using var fixture = new Connections();
        TaskCompletionSource allowConnect = fixture.Gate();
        Task<AudioPlayer> first = fixture.GetAsync(connectWait: allowConnect.Task);
        await fixture.Created[0].ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<AudioPlayer> second = fixture.GetAsync();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.False(fixture.Service.TryGetAudioPlayer(0, out _));
        Assert.Single(fixture.Created);
        allowConnect.SetResult();

        Assert.Same(await first.WaitAsync(TimeSpan.FromSeconds(5)), await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(fixture.Created);
    }

    [Fact]
    public async Task RejoiningWaitsForTheOldConnectionToFinishDisconnecting()
    {
        await using var fixture = new Connections();
        TaskCompletionSource allowStop = fixture.Gate();
        AudioPlayer first = await fixture.GetAsync(stopWait: allowStop.Task);
        Task disconnect = fixture.Service.HandleVoiceChannelChangedAsync(0, 0, null);
        await fixture.Created[0].Transport.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<AudioPlayer> rejoin = fixture.GetAsync();

        Assert.False(fixture.Service.TryGetAudioPlayer(0, out _));
        Assert.False(rejoin.IsCompleted);
        Assert.Single(fixture.Created);
        allowStop.SetResult();
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotSame(first, await rejoin.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Created[0].DisconnectCalls);
        Assert.Equal(0, fixture.Created[1].DisconnectCalls);
    }

    [Fact]
    public async Task FailedJoinsPropagateTheirOriginalErrorAndCanBeRetried()
    {
        await using var fixture = new Connections();
        var failure = new IOException("Connection failed");

        IOException error = await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(connectError: failure));

        Assert.Same(failure, error);
        Assert.Empty(fixture.Players);
        Assert.Equal(1, fixture.Created[0].DisconnectCalls);
        Assert.True((await fixture.GetAsync()).IsReady);
    }

    [Fact]
    public async Task AlreadyDisconnectedClientsAreRejectedEvenIfTheirDisconnectEventWasMissed()
    {
        await using var fixture = new Connections();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.GetAsync(disconnectBeforeReady: true));

        Assert.Empty(fixture.Players);
        Assert.True(fixture.Created[0].Transport.Disposed);
        Assert.Null(fixture.Created[0].Transport.Disconnected);
        Assert.True((await fixture.GetAsync()).IsReady);
    }

    [Fact]
    public async Task DisconnectDuringJoiningCleansTheLateClientBeforeAllowingRejoin()
    {
        await using var fixture = new Connections();
        TaskCompletionSource allowConnect = fixture.Gate();
        Task<AudioPlayer> joining = fixture.GetAsync(connectWait: allowConnect.Task);
        Connection old = fixture.Created[0];
        await old.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disconnect = fixture.Service.HandleVoiceChannelChangedAsync(0, 0, null);
        Task<AudioPlayer> rejoin = fixture.GetAsync();

        Assert.False(disconnect.IsCompleted);
        Assert.False(rejoin.IsCompleted);
        Assert.Single(fixture.Players);
        allowConnect.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => joining.WaitAsync(TimeSpan.FromSeconds(5)));
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await rejoin.WaitAsync(TimeSpan.FromSeconds(5))).IsReady);
        Assert.Equal(1, old.Transport.StopCalls);
        Assert.True(old.Transport.Disposed);
        Assert.Equal(1, old.DisconnectCalls);
        Assert.Equal(0, fixture.Created[1].DisconnectCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportDisconnectWhileIdleStartsCleanupWithoutBlockingTheCallback(bool withError)
    {
        await using var fixture = new Connections();
        TaskCompletionSource allowStop = fixture.Gate();
        AudioPlayer first = await fixture.GetAsync(stopWait: allowStop.Task);
        Connection old = fixture.Created[0];

        await old.Transport.RaiseDisconnectedAsync(withError ? new IOException("Lost transport") : null)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await old.Transport.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<AudioPlayer> rejoin = fixture.GetAsync();

        Assert.False(fixture.Service.TryGetAudioPlayer(0, out _));
        Assert.False(rejoin.IsCompleted);
        allowStop.SetResult();

        Assert.NotSame(first, await rejoin.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(old.Transport.Output.Disposed);
        Assert.Null(old.Transport.Disconnected);
    }

    [Fact]
    public async Task DisconnectCleanupFailuresAreLoggedAndDoNotPreventRejoining()
    {
        await using var fixture = new Connections();
        await fixture.GetAsync(disconnectError: new IOException("Failed to leave voice"));

        await fixture.Service.HandleVoiceChannelChangedAsync(0, 0, null);

        Assert.Empty(fixture.Players);
        Assert.True(fixture.Created[0].Transport.Disposed);
        Assert.True(fixture.Created[0].Transport.Output.Disposed);
        List<LogDbEntry> entries = [];

        while (fixture.Logs.Reader.TryRead(out LogDbEntry? entry))
        {
            entries.Add(entry);
        }

        Assert.Contains(entries, entry => entry.Content.Contains("Voice disconnect cleanup failed", StringComparison.Ordinal));
        Assert.True((await fixture.GetAsync()).IsReady);
    }

    private sealed class Connections : IAsyncDisposable
    {
        public ConcurrentDictionary<ulong, AudioPlayer> Players { get; } = new();
        public Channel<LogDbEntry> Logs { get; } = Channel.CreateUnbounded<LogDbEntry>();
        public List<Connection> Created { get; } = [];
        public AudioService Service { get; }

        private readonly Logger _logger;
        private readonly SocketGuild _guild = (SocketGuild)RuntimeHelpers.GetUninitializedObject(typeof(SocketGuild));
        private readonly SocketVoiceChannel _channel = (SocketVoiceChannel)RuntimeHelpers.GetUninitializedObject(typeof(SocketVoiceChannel));
        private readonly List<TaskCompletionSource> _gates = [];

        public Connections()
        {
            _logger = (Logger)RuntimeHelpers.GetUninitializedObject(typeof(Logger));
            typeof(Logger).GetField("LogChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_logger, Logs);
            Service = new AudioService(_logger, Players);
        }

        public TaskCompletionSource Gate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates.Add(gate);
            return gate;
        }

        public Task<AudioPlayer> GetAsync(Task? connectWait = null, Task? stopWait = null,
            Exception? connectError = null, Exception? disconnectError = null, bool disconnectBeforeReady = false)
        {
            Connection? connection = null;
            return Service.GetOrCreateAudioPlayerAsync(0, () =>
            {
                IAudioClient client = DispatchProxy.Create<IAudioClient, AudioClientProxy>();
                var transport = Assert.IsAssignableFrom<AudioClientProxy>(client);
                transport.StopWait = stopWait;
                var player = new AudioPlayer(null!, new(), _logger, _guild, null!, Players, null!,
                    new AudioRecommendationService(null!, []));
                connection = new Connection(player, client, transport);
                Created.Add(connection);
                return Task.FromResult(player);
            }, player => player.JoinChannelAsync(_channel, async () =>
            {
                connection!.ConnectStarted.TrySetResult();

                if (connectWait is not null)
                {
                    await connectWait;
                }

                if (connectError is not null)
                {
                    throw connectError;
                }

                if (disconnectBeforeReady)
                {
                    await connection.Transport.RaiseDisconnectedAsync(null);
                }

                return connection.Client;
            }, () =>
            {
                connection!.DisconnectCalls++;

                if (disconnectError is not null)
                {
                    throw disconnectError;
                }

                return Task.CompletedTask;
            })).AsTask();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (TaskCompletionSource gate in _gates)
            {
                gate.TrySetResult();
            }

            foreach (Connection connection in Created)
            {
                try
                {
                    await connection.Player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // Failure-path tests have already checked the reported cleanup errors.
                }
            }
        }
    }

    private sealed class Connection(AudioPlayer player, IAudioClient client, AudioClientProxy transport)
    {
        public AudioPlayer Player { get; } = player;
        public IAudioClient Client { get; } = client;
        public AudioClientProxy Transport { get; } = transport;
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisconnectCalls { get; set; }
    }

    public class AudioClientProxy : DispatchProxy
    {
        public Func<Exception, Task>? Disconnected { get; private set; }
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? StopWait { get; set; }
        public int StopCalls { get; private set; }
        public bool Disposed { get; private set; }
        public TestAudioOutStream Output { get; } = new();
        private ConnectionState _state = ConnectionState.Connected;

        public Task RaiseDisconnectedAsync(Exception? error)
        {
            _state = ConnectionState.Disconnected;
            return Task.WhenAll(Disconnected?.GetInvocationList().Cast<Func<Exception, Task>>().Select(handler => handler(error!)) ?? []);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_ConnectionState":
                    return _state;
                case "add_Disconnected":
                    Disconnected += (Func<Exception, Task>)args![0]!;
                    return null;
                case "remove_Disconnected":
                    Disconnected -= (Func<Exception, Task>)args![0]!;
                    return null;
                case nameof(IAudioClient.CreateDirectPCMStream):
                    return Output;
                case nameof(IAudioClient.SetSpeakingAsync):
                    return Task.CompletedTask;
                case nameof(IAudioClient.StopAsync):
                    StopCalls++;
                    StopStarted.TrySetResult();
                    return StopAsync();
                case nameof(IDisposable.Dispose):
                    Disposed = true;
                    return null;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }

        private async Task StopAsync()
        {
            if (StopWait is not null)
            {
                await StopWait;
            }

            await RaiseDisconnectedAsync(null);
        }
    }

    public sealed class TestAudioOutStream : AudioOutStream
    {
        public bool Disposed { get; private set; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
