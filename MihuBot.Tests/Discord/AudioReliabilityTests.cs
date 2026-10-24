using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Discord.WebSocket;
using MihuBot.Discord.Audio;

namespace MihuBot.Tests.Discord;

public sealed class AudioReliabilityTests
{
    [Fact]
    public async Task StaleAutoplayStopDoesNotSkipNewManualTrack()
    {
        await using var scheduler = new AudioScheduler();
        var autoplay = new ControlledSource(isAutoplay: true);
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));
        Assert.Same(autoplay, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        scheduler.GetAutoplayState(out IAudioSource observed, out _);
        var manual = new ControlledSource();
        scheduler.Enqueue(manual);
        scheduler.SkipCurrent(autoplay);
        Assert.Same(manual, await scheduler.GetAudioSourceAsync(CancellationToken.None));

        Assert.False(await scheduler.TrySkipAsync(observed));
        Assert.Equal(0, manual.CleanupCalls);
        Assert.True(scheduler.TryPeekCurrent(out IAudioSource current));
        Assert.Same(manual, current);
    }

    [Fact]
    public async Task StaleAutoplayCleanupFailureDoesNotLoseInitializingManualTrack()
    {
        await using var scheduler = new AudioScheduler();
        var autoplay = new ControlledSource(cleanup: () => throw new IOException("Cleanup failure"), isAutoplay: true);
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));
        var release = Signal();
        var manual = new ControlledSource(_ => release.Task);
        scheduler.Enqueue(manual);

        await Assert.ThrowsAsync<AudioSourceException>(() => scheduler.GetAudioSourceAsync(CancellationToken.None).AsTask());

        release.SetResult();
        Assert.Same(manual, await scheduler.GetAudioSourceAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, autoplay.CleanupCalls);
    }

    [Fact]
    public async Task DisposalFailureDoesNotLeaveOtherSourcesUndisposed()
    {
        var scheduler = new AudioScheduler();
        var failing = new ControlledSource(cleanup: () => throw new IOException("Cleanup failure"));
        var other = new ControlledSource();
        scheduler.Enqueue(failing);
        scheduler.Enqueue(other);

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => scheduler.DisposeAsync().AsTask());
        Assert.IsType<IOException>(Assert.Single(error.Flatten().InnerExceptions));
        Assert.Equal(1, failing.CleanupCalls);
        Assert.Equal(1, other.CleanupCalls);

        await Assert.ThrowsAsync<AggregateException>(() => scheduler.DisposeAsync().AsTask());
        Assert.Equal(1, failing.CleanupCalls);
        Assert.Equal(1, other.CleanupCalls);
    }

    [Fact]
    public async Task FailedPlayerCleanupUnregistersPlayerAndAllCallersAwaitIt()
    {
        var guild = (SocketGuild)RuntimeHelpers.GetUninitializedObject(typeof(SocketGuild));
        var players = new ConcurrentDictionary<ulong, AudioPlayer>();
        var player = new AudioPlayer(null!, new(), null!, guild, null!, players, null!, null!);
        players[guild.Id] = player;
        var failing = new ControlledSource(cleanup: () => throw new IOException("Cleanup failure"));
        player.Enqueue(failing);

        Task first = player.DisposeAsync().AsTask();
        Task second = player.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<AggregateException>(() => first);
        await Assert.ThrowsAsync<AggregateException>(() => second);

        Assert.False(players.ContainsKey(guild.Id));
        Assert.Equal(1, failing.CleanupCalls);
    }

    [Fact]
    public async Task DisposalWaitsForLateInitializationAndCleansItsResourcesOnce()
    {
        var started = Signal();
        var release = Signal();
        bool resourceCreated = false;
        var source = new ControlledSource(async _ =>
        {
            started.SetResult();
            await release.Task;
            resourceCreated = true;
        }, () =>
        {
            Assert.True(resourceCreated);
            return Task.CompletedTask;
        });
        source.StartInitializing(128);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task first = source.DisposeAsync().AsTask();
        Task second = source.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.CleanupCalls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.EnsureInitializedAsync());
    }

    [Fact]
    public async Task DisposedSourceCannotStartInitialization()
    {
        bool initialized = false;
        var source = new ControlledSource(_ =>
        {
            initialized = true;
            return Task.CompletedTask;
        });
        await source.DisposeAsync();
        source.StartInitializing(128);

        Assert.False(initialized);
        Assert.Equal(1, source.CleanupCalls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.EnsureInitializedAsync());
    }

    [Fact]
    public async Task FailedPrefetchCleansResourcesWithoutWaitingToBeDequeued()
    {
        var cleaned = Signal();
        var source = new ControlledSource(_ => throw new IOException("Initialization failure"), () =>
        {
            cleaned.SetResult();
            return Task.CompletedTask;
        });
        source.StartInitializing(128);

        await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => source.EnsureInitializedAsync());
        await source.DisposeAsync();
        Assert.Equal(1, source.CleanupCalls);
    }

    [Fact]
    public async Task InitializationTimeoutReleasesTheQueueForTheNextManualTrack()
    {
        await using var scheduler = new AudioScheduler();
        var stalled = new ControlledSource(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token), initializationTimeout: TimeSpan.FromMilliseconds(20));
        var next = new ControlledSource();
        scheduler.Enqueue(stalled);
        scheduler.Enqueue(next);

        AudioSourceException error = await Assert.ThrowsAsync<AudioSourceException>(() =>
            scheduler.GetAudioSourceAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Equal(1, stalled.CleanupCalls);
        Assert.Same(next, await scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SkippingDuringInitializationCancelsItAndPreservesTheNextTrack()
    {
        await using var scheduler = new AudioScheduler();
        var started = Signal();
        var stalled = new ControlledSource(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var next = new ControlledSource();
        scheduler.Enqueue(stalled);
        scheduler.Enqueue(next);
        Task<IAudioSource> selecting = scheduler.GetAudioSourceAsync(CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.SkipAsync();

        Assert.Same(next, await selecting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, stalled.CleanupCalls);
    }

    [Fact]
    public async Task DisposalDuringInitializationCancelsSelectionAndJoinsCleanup()
    {
        var scheduler = new AudioScheduler();
        var started = Signal();
        var stalled = new ControlledSource(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        scheduler.Enqueue(stalled);
        Task<IAudioSource> selecting = scheduler.GetAudioSourceAsync(CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => selecting);
        Assert.Equal(1, stalled.CleanupCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadPumpClosesFfmpegInputOnCompletionOrFailure(bool fail)
    {
        Stream download = fail ? new FailingReadStream() : new MemoryStream([1, 2, 3, 4]);
        var input = new MemoryStream();

        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() =>
                YoutubeAudioSource.PumpInputAsync(download, input, CancellationToken.None));
        }
        else
        {
            await YoutubeAudioSource.PumpInputAsync(download, input, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, input.ToArray());
        }

        Assert.False(input.CanWrite);
        await download.DisposeAsync();
    }

    [Fact]
    public async Task ReadAheadPropagatesNetworkFailure()
    {
        await using var readAhead = new ReadAheadStream(new FailingReadStream());

        await Assert.ThrowsAsync<IOException>(() => readAhead.ReadAsync(new byte[16]).AsTask());
    }

    [Fact]
    public async Task ReadAheadDisposalInterruptsBackpressuredDownloads()
    {
        for (int i = 0; i < 20; i++)
        {
            var download = new ProgressMemoryStream();
            var readAhead = new ReadAheadStream(download, bufferCapacity: 64 * 1024);
            await download.FilledBuffer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await readAhead.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(download.CanRead);
        }
    }

    [Fact]
    public async Task FrameConsumerFailureUnblocksWaitingProducers()
    {
        var entered = Signal();
        var release = Signal();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var timer = new MediaTimer<int>(50, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1),
            error => reported.TrySetResult(error));
        timer.OnFrameAsync = async _ =>
        {
            entered.SetResult();
            await release.Task;
            throw new IOException("Disconnected transport");
        };
        await timer.EnqueueAsync(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.EnqueueAsync(2);
        Task producer = timer.EnqueueAsync(3);
        Assert.False(producer.IsCompleted);

        release.SetResult();

        IOException failure = await Assert.ThrowsAsync<IOException>(() => producer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FrameTimeoutIsReportedAndRejectsFurtherWrites()
    {
        var release = Signal();
        var callbackFinished = Signal();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var timer = new MediaTimer<int>(50, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1),
            error => failed.TrySetResult(error), callbackTimeout: TimeSpan.FromMilliseconds(20));
        timer.OnFrameAsync = async _ =>
        {
            await release.Task;
            callbackFinished.SetResult();
        };
        await timer.EnqueueAsync(1);

        Assert.IsType<TimeoutException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<TimeoutException>(() => timer.EnqueueAsync(2));
        release.SetResult();
        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FrameDisposalUnblocksProducersAndReleasesQueuedFramesWithoutWaitingForTransport()
    {
        var entered = Signal();
        var release = Signal();
        var callbackFinished = Signal();
        var cleared = new List<int>();
        var timer = new MediaTimer<int>(50, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1));
        timer.OnFrameAsync = async _ =>
        {
            entered.SetResult();
            await release.Task;
            callbackFinished.SetResult();
        };
        timer.OnClearedFrameAsync = frame =>
        {
            cleared.Add(frame);
            return Task.CompletedTask;
        };
        await timer.EnqueueAsync(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.EnqueueAsync(2);
        Task producer = timer.EnqueueAsync(3);

        await timer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer);
        Assert.Equal([2], cleared);
        release.SetResult();
        await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(163)]
    public async Task ConcurrentAddsSkipsFailuresAndShutdownReleaseEveryOwnedSource(int seed)
    {
        var scheduler = new AudioScheduler();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sources = new ConcurrentBag<ControlledSource>();
        int id = 0;

        ControlledSource CreateSource(bool autoplay)
        {
            int sourceId = Interlocked.Increment(ref id);
            var source = new ControlledSource(async token =>
            {
                await Task.Delay(sourceId % 3, token);

                if (sourceId % 7 == 0)
                {
                    throw new IOException("Planned initialization failure");
                }
            }, () => sourceId % 11 == 0
                ? Task.FromException(new IOException("Planned cleanup failure"))
                : Task.CompletedTask, autoplay);
            sources.Add(source);
            return source;
        }

        static IEnumerable<Exception> Flatten(Exception error) =>
            error switch
            {
                AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten),
                AudioSourceException source => Flatten(source.InnerException!),
                _ => [error]
            };

        async Task AllowPlannedFailures(Func<Task> operation)
        {
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                Assert.All(Flatten(ex), error =>
                    Assert.True(error is IOException || (error is OperationCanceledException && stop.IsCancellationRequested), error.ToString()));
            }
        }

        Task consumer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await AllowPlannedFailures(async () =>
                {
                    IAudioSource source = await scheduler.GetAudioSourceAsync(stop.Token);
                    await Task.Yield();
                    scheduler.SkipCurrent(source);
                    await source.DisposeAsync();
                });
            }
        });

        Task[] producers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            var random = new Random(seed + worker);

            for (int i = 0; i < 100 && !stop.IsCancellationRequested; i++)
            {
                await AllowPlannedFailures(async () =>
                {
                    switch (random.Next(5))
                    {
                        case 0:
                            scheduler.Enqueue(CreateSource(autoplay: false));
                            break;
                        case 1:
                            var candidate = CreateSource(autoplay: true);

                            if (!scheduler.TrySetAutoplay(candidate, scheduler.Revision))
                            {
                                await candidate.DisposeAsync();
                            }

                            break;
                        case 2:
                            await scheduler.SkipAsync();
                            break;
                        case 3:
                            if (scheduler.TryPeekCurrent(out IAudioSource observed))
                            {
                                await Task.Yield();
                                await scheduler.TrySkipAsync(observed);
                            }

                            break;
                        case 4:
                            await scheduler.ClearAutoplayAsync();
                            break;
                    }
                });
                await Task.Yield();
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            stop.Cancel();
            await AllowPlannedFailures(() => scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.Equal(1, source.CleanupCalls));
    }

    [Fact]
    public async Task CountedSkipRemovesCurrentAndQueuedTracksAtomically()
    {
        await using var scheduler = new AudioScheduler();
        ControlledSource[] sources = Enumerable.Range(0, 5).Select(_ => new ControlledSource()).ToArray();

        foreach (ControlledSource source in sources)
        {
            scheduler.Enqueue(source);
        }

        Assert.Same(sources[0], await scheduler.GetAudioSourceAsync(CancellationToken.None));
        IAudioSource? marked = null;
        AudioSkipBatch batch = scheduler.SkipTracks(3, Stopwatch.GetTimestamp(), onSkippingCurrent: source =>
        {
            Assert.True(scheduler.TryPeekCurrent(out IAudioSource current));
            Assert.Same(current, source);
            marked = source;
        });
        await AudioScheduler.DisposeSourcesAsync(batch.ToDispose);

        Assert.Equal(3, batch.Count);
        Assert.Same(sources[0], marked);
        Assert.Same(sources[0], batch.Current);
        Assert.Equal(sources.Skip(3).Cast<IAudioSource>(), scheduler.GetPlaybackSnapshot());
        Assert.Same(sources[3], await scheduler.GetAudioSourceAsync(CancellationToken.None));
        Assert.All(sources.Take(3), source => Assert.Equal(1, source.CleanupCalls));
        Assert.All(sources.Skip(3), source => Assert.Equal(0, source.CleanupCalls));
    }

    [Fact]
    public async Task SkipUntilPreservesTheDestinationAndMissingTargetsDoNotChangeTheQueue()
    {
        await using var scheduler = new AudioScheduler();
        var first = new ControlledSource();
        var second = new ControlledSource();
        var destination = new ControlledSource();
        await using var missing = new ControlledSource();
        scheduler.Enqueue(first);
        scheduler.Enqueue(second);
        scheduler.Enqueue(destination);
        Assert.Same(first, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        long revision = scheduler.Revision;

        AudioSkipBatch notFound = scheduler.SkipTracks(1, Stopwatch.GetTimestamp(), missing);
        Assert.True(notFound.TargetMissing);
        Assert.Equal(revision, scheduler.Revision);
        Assert.Equal([first, second, destination], scheduler.GetPlaybackSnapshot());

        AudioSkipBatch batch = scheduler.SkipTracks(1, Stopwatch.GetTimestamp(), destination);
        await AudioScheduler.DisposeSourcesAsync(batch.ToDispose);

        Assert.Equal(2, batch.Count);
        Assert.Equal(1, first.CleanupCalls);
        Assert.Equal(1, second.CleanupCalls);
        Assert.Equal(0, destination.CleanupCalls);
        Assert.Same(destination, await scheduler.GetAudioSourceAsync(CancellationToken.None));

        AudioSkipBatch alreadyCurrent = scheduler.SkipTracks(1, Stopwatch.GetTimestamp(), destination);
        Assert.Equal(0, alreadyCurrent.Count);
        Assert.Empty(alreadyCurrent.ToDispose);
        Assert.False(alreadyCurrent.TargetMissing);
        Assert.True(scheduler.TryPeekCurrent(out IAudioSource current));
        Assert.Same(destination, current);
    }

    [Fact]
    public async Task BulkSkipCancelsInitializationAndDisposesSkippedSourcesWithoutSkippingTheDestination()
    {
        await using var scheduler = new AudioScheduler();
        var started = Signal();
        var stalled = new ControlledSource(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var next = new ControlledSource();
        var destination = new ControlledSource();
        scheduler.Enqueue(stalled);
        scheduler.Enqueue(next);
        scheduler.Enqueue(destination);
        Task<IAudioSource> selecting = scheduler.GetAudioSourceAsync(CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(stalled, scheduler.GetPlaybackSnapshot()[0]);

        AudioSkipBatch batch = scheduler.SkipTracks(2, Stopwatch.GetTimestamp());
        await AudioScheduler.DisposeSourcesAsync(batch.ToDispose);

        Assert.Equal(2, batch.Count);
        Assert.Same(destination, await selecting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, stalled.CleanupCalls);
        Assert.Equal(1, next.CleanupCalls);
        Assert.Equal(0, destination.CleanupCalls);
    }

    [Fact]
    public async Task SkipsSpokenBeforeThePreviousActionAreIgnoredButNewSpeechIsAccepted()
    {
        await using var scheduler = new AudioScheduler();
        var first = new ControlledSource();
        var second = new ControlledSource();
        var third = new ControlledSource();
        scheduler.Enqueue(first);
        scheduler.Enqueue(second);
        scheduler.Enqueue(third);
        Assert.Same(first, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        long capturedBeforeAction = Stopwatch.GetTimestamp();
        AudioSkipBatch initial = scheduler.SkipTracks(1, capturedBeforeAction);
        await AudioScheduler.DisposeSourcesAsync(initial.ToDispose);
        Assert.Same(second, await scheduler.GetAudioSourceAsync(CancellationToken.None));

        AudioSkipBatch duplicate = scheduler.SkipTracks(1, capturedBeforeAction);
        Assert.True(duplicate.Ignored);
        Assert.Empty(duplicate.ToDispose);
        Assert.Equal(0, second.CleanupCalls);
        Assert.True(scheduler.TryPeekCurrent(out IAudioSource unchanged));
        Assert.Same(second, unchanged);

        AudioSkipBatch fresh = scheduler.SkipTracks(1, Stopwatch.GetTimestamp());
        await AudioScheduler.DisposeSourcesAsync(fresh.ToDispose);
        Assert.False(fresh.Ignored);
        Assert.Equal(1, fresh.Count);
        Assert.Equal(1, second.CleanupCalls);
        Assert.Same(third, await scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AManualSkipAlsoInvalidatesPreviouslyCapturedVoiceSkips()
    {
        await using var scheduler = new AudioScheduler();
        var first = new ControlledSource();
        var second = new ControlledSource();
        scheduler.Enqueue(first);
        scheduler.Enqueue(second);
        Assert.Same(first, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        long capturedBeforeAction = Stopwatch.GetTimestamp();
        await scheduler.SkipAsync();

        AudioSkipBatch batch = scheduler.SkipTracks(1, capturedBeforeAction);

        Assert.True(batch.Ignored);
        Assert.Equal(0, second.CleanupCalls);
        Assert.Same(second, await scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACountLargerThanTheQueueSkipsOnlyAvailableTracksAndInvalidatesPrefetchedAutoplay()
    {
        await using var scheduler = new AudioScheduler();
        var current = new ControlledSource();
        var autoplay = new ControlledSource(isAutoplay: true);
        scheduler.Enqueue(current);
        Assert.Same(current, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));

        AudioSkipBatch batch = scheduler.SkipTracks(3, Stopwatch.GetTimestamp());
        await AudioScheduler.DisposeSourcesAsync(batch.ToDispose);

        Assert.Equal(1, batch.Count);
        Assert.Empty(scheduler.GetPlaybackSnapshot());
        Assert.Equal(1, current.CleanupCalls);
        Assert.Equal(1, autoplay.CleanupCalls);
        Assert.False(scheduler.HasAutoplay);
    }

    [Fact]
    public async Task BulkCleanupFailureStillDisposesOtherSkippedTracksAndPreservesTheTarget()
    {
        await using var scheduler = new AudioScheduler();
        var failing = new ControlledSource(cleanup: () => throw new IOException("Cleanup failure"));
        var other = new ControlledSource();
        var target = new ControlledSource();
        scheduler.Enqueue(failing);
        scheduler.Enqueue(other);
        scheduler.Enqueue(target);
        Assert.Same(failing, await scheduler.GetAudioSourceAsync(CancellationToken.None));

        AudioSkipBatch batch = scheduler.SkipTracks(2, Stopwatch.GetTimestamp());
        await Assert.ThrowsAsync<AggregateException>(() => AudioScheduler.DisposeSourcesAsync(batch.ToDispose));

        Assert.Equal(1, failing.CleanupCalls);
        Assert.Equal(1, other.CleanupCalls);
        Assert.Equal(0, target.CleanupCalls);
        Assert.Same(target, await scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledSource(
        Func<CancellationToken, Task>? initialize = null,
        Func<Task>? cleanup = null,
        bool isAutoplay = false,
        TimeSpan? initializationTimeout = null)
        : AudioSourceBase(null!, isAutoplay, initializationTimeout ?? TimeSpan.FromSeconds(30))
    {
        public int CleanupCalls { get; private set; }
        public override string Description => "Controlled source";
        public override string Url => "https://example.com";
        public override string ThumbnailUrl => "";
        public override TimeSpan? Remaining => TimeSpan.FromSeconds(10);
        public override TimeSpan? Duration => TimeSpan.FromSeconds(10);

        protected override Task InitializeAsync(int bitrateHintKbit, CancellationToken cancellationToken) =>
            initialize?.Invoke(cancellationToken) ?? Task.CompletedTask;

        protected override async ValueTask DisposeResourcesAsync()
        {
            CleanupCalls++;

            if (cleanup is not null)
            {
                await cleanup();
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Disconnected download");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Disconnected download"));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ProgressMemoryStream() : MemoryStream(new byte[128 * 1024])
    {
        public TaskCompletionSource FilledBuffer { get; } = Signal();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask<int> result = base.ReadAsync(buffer, cancellationToken);

            if (Position >= 64 * 1024)
            {
                FilledBuffer.TrySetResult();
            }

            return result;
        }
    }
}
