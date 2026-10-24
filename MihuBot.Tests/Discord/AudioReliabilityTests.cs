using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Discord.WebSocket;
using MihuBot.Discord.Audio;
using MihuBot.Helpers;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json;

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
        Assert.Same(stalled, error.AudioSource);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task YoutubeDownloadPreservesHeadersAndStreamsUntilDisposed(bool metadataHeaders, bool formatHeaders)
    {
        var metadata = new YoutubeDl.YoutubeDlMetadata
        {
            HttpHeaders = metadataHeaders
                ? new() { ["User-Agent"] = "metadata-agent", ["Referer"] = "https://www.youtube.com/" }
                : null
        };
        var format = new YoutubeDl.YoutubeDlFormat
        {
            Url = "https://audio.example/stream",
            HttpHeaders = formatHeaders
                ? new() { ["user-agent"] = "format-agent", ["Origin"] = "https://www.youtube.com" }
                : null
        };
        using var body = new MemoryStream([1, 2, 3, 4]);
        using var http = new HttpClient(new DownloadHandler((request, _) =>
        {
            Assert.Equal(format.Url, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);

            if (metadataHeaders || formatHeaders)
            {
                Assert.Equal(formatHeaders ? "format-agent" : "metadata-agent",
                    Assert.Single(request.Headers.GetValues("User-Agent")));
            }
            else
            {
                Assert.False(request.Headers.Contains("User-Agent"));
            }

            Assert.Equal(metadataHeaders, request.Headers.Contains("Referer"));
            Assert.Equal(formatHeaders, request.Headers.Contains("Origin"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }));

        using (HttpResponseMessage response = await YoutubeAudioSource.OpenDownloadAsync(http, metadata, format, CancellationToken.None))
        {
            Assert.True(body.CanRead);
            Assert.Equal(0, body.Position);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        }

        Assert.False(body.CanRead);
    }

    [Theory]
    [InlineData('\r', false, false)]
    [InlineData('\n', false, false)]
    [InlineData('\0', false, false)]
    [InlineData('\r', true, false)]
    [InlineData('\n', true, false)]
    [InlineData('\0', true, false)]
    [InlineData('\r', false, true)]
    [InlineData('\n', false, true)]
    [InlineData('\0', false, true)]
    [InlineData('\r', true, true)]
    [InlineData('\n', true, true)]
    [InlineData('\0', true, true)]
    public async Task YoutubeDownloadRejectsControlCharactersInHeaderNamesAndValues(char character, bool inName, bool formatHeader)
    {
        var headers = new Dictionary<string, string>
        {
            [inName ? $"X-Test{character}Header" : "X-Test-Header"] = inName ? "value" : $"test{character}value"
        };
        var metadata = new YoutubeDl.YoutubeDlMetadata { HttpHeaders = formatHeader ? null : headers };
        var format = new YoutubeDl.YoutubeDlFormat
        {
            Url = "https://audio.example/stream",
            HttpHeaders = formatHeader ? headers : null
        };
        bool sent = false;
        using var http = new HttpClient(new DownloadHandler((_, _) =>
        {
            sent = true;
            throw new InvalidOperationException("Invalid headers must not reach the HTTP handler.");
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            YoutubeAudioSource.OpenDownloadAsync(http, metadata, format, CancellationToken.None));

        Assert.False(sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task YoutubeDownloadPropagatesHttpFailuresAndDisposesTheResponse(HttpStatusCode status)
    {
        using var body = new MemoryStream([1, 2, 3, 4]);
        using var http = new HttpClient(new DownloadHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StreamContent(body) })));

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            YoutubeAudioSource.OpenDownloadAsync(http, new(), new() { Url = "https://audio.example/stream" }, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.False(body.CanRead);
    }

    [Fact]
    public async Task YoutubeDownloadWaitsForFormatAvailabilityBeforeSending()
    {
        var time = new FakeTimeProvider();
        var format = JsonConvert.DeserializeObject<YoutubeDl.YoutubeDlFormat>(
            $$"""
            {
                "url": "https://audio.example/stream",
                "available_at": {{time.GetUtcNow().AddSeconds(5).ToUnixTimeSeconds()}}
            }
            """)!;
        bool sent = false;
        using var http = new HttpClient(new DownloadHandler((_, _) =>
        {
            sent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        Task<HttpResponseMessage> download = YoutubeAudioSource.OpenDownloadAsync(http, new(), format, CancellationToken.None, time);

        Assert.False(sent);
        time.Advance(TimeSpan.FromSeconds(4.999));
        Assert.False(sent);
        Assert.False(download.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));

        using HttpResponseMessage response = await download.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task YoutubeDownloadDoesNotWaitForAlreadyAvailableFormat(int seconds)
    {
        var time = new FakeTimeProvider();
        var format = new YoutubeDl.YoutubeDlFormat
        {
            Url = "https://audio.example/stream",
            AvailableAt = time.GetUtcNow().AddSeconds(seconds).ToUnixTimeSeconds()
        };
        using var http = new HttpClient(new DownloadHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        using HttpResponseMessage response = await YoutubeAudioSource.OpenDownloadAsync(http, new(), format, CancellationToken.None, time)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task YoutubeDownloadCancellationInterruptsAvailabilityWaitWithoutSending()
    {
        var time = new FakeTimeProvider();
        var format = new YoutubeDl.YoutubeDlFormat
        {
            Url = "https://audio.example/stream",
            AvailableAt = time.GetUtcNow().AddSeconds(5).ToUnixTimeSeconds()
        };
        bool sent = false;
        using var http = new HttpClient(new DownloadHandler((_, _) =>
        {
            sent = true;
            throw new InvalidOperationException("A canceled download must not run.");
        }));
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> download = YoutubeAudioSource.OpenDownloadAsync(http, new(), format, cancellation.Token, time);
        Assert.False(download.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(sent);
    }

    [Fact]
    public async Task YoutubeDownloadHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new DownloadHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("A canceled download must not run.");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            YoutubeAudioSource.OpenDownloadAsync(http, new(), new() { Url = "https://audio.example/stream" }, cancellation.Token));
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

    [Fact]
    public async Task QueueEditsReorderUpcomingTracksAndDisposeOnlyRemovedSources()
    {
        await using var scheduler = new AudioScheduler();
        var current = new ControlledSource();
        var first = new ControlledSource();
        var removed = new ControlledSource();
        var next = new ControlledSource();
        scheduler.Enqueue(current);
        scheduler.Enqueue(first);
        scheduler.Enqueue(removed);
        scheduler.Enqueue(next);
        Assert.Same(current, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        var snapshot = scheduler.GetEditableQueueSnapshot();
        long revision = scheduler.Revision;

        await scheduler.ReplaceQueueAsync(snapshot.Queue, [next, first]);

        Assert.Same(current, scheduler.GetEditableQueueSnapshot().Current);
        Assert.Equal([next, first], scheduler.GetEditableQueueSnapshot().Queue);
        Assert.True(scheduler.Revision > revision);
        Assert.Equal(1, removed.CleanupCalls);
        Assert.Equal(0, current.CleanupCalls);
        Assert.Equal(0, next.CleanupCalls);
        await scheduler.SkipAsync();
        Assert.Same(next, await scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueueEditsRejectSnapshotsThatChangedDuringPlayback()
    {
        await using var scheduler = new AudioScheduler();
        var first = new ControlledSource();
        var second = new ControlledSource();
        scheduler.Enqueue(first);
        scheduler.Enqueue(second);
        IAudioSource[] expected = scheduler.GetEditableQueueSnapshot().Queue;
        Assert.Same(first, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        long revision = scheduler.Revision;

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ReplaceQueueAsync(expected, []));

        Assert.Equal(revision, scheduler.Revision);
        Assert.Same(first, scheduler.GetEditableQueueSnapshot().Current);
        Assert.Equal([second], scheduler.GetEditableQueueSnapshot().Queue);
        Assert.Equal(0, first.CleanupCalls);
        Assert.Equal(0, second.CleanupCalls);
    }

    [Fact]
    public async Task QueueEditsPreserveTheTrackWhoseInitializationIsInProgress()
    {
        await using var scheduler = new AudioScheduler();
        var started = Signal();
        var ready = Signal();
        var initializing = new ControlledSource(async token =>
        {
            started.TrySetResult();
            await ready.Task.WaitAsync(token);
        });
        var removed = new ControlledSource();
        scheduler.Enqueue(initializing);
        scheduler.Enqueue(removed);
        Task<IAudioSource> selection = scheduler.GetAudioSourceAsync(CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = scheduler.GetEditableQueueSnapshot();

        Assert.Same(initializing, snapshot.Current);
        await scheduler.ReplaceQueueAsync(snapshot.Queue, []);

        Assert.Equal(1, removed.CleanupCalls);
        Assert.Equal(0, initializing.CleanupCalls);
        ready.TrySetResult();
        Assert.Same(initializing, await selection.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ClearingAnEmptyUpcomingQueueDoesNotInvalidatePreparedAutoplay()
    {
        await using var scheduler = new AudioScheduler();
        var autoplay = new ControlledSource(isAutoplay: true);
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));
        long revision = scheduler.Revision;
        var snapshot = scheduler.GetEditableQueueSnapshot();

        await scheduler.ReplaceQueueAsync(snapshot.Queue, []);

        Assert.Same(autoplay, snapshot.Current);
        Assert.Equal(revision, scheduler.Revision);
        Assert.True(scheduler.HasAutoplay);
        Assert.Equal(0, autoplay.CleanupCalls);
        Assert.Same(autoplay, await scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueueEditsCannotDuplicateOrInsertTheCurrentTrack()
    {
        await using var scheduler = new AudioScheduler();
        var current = new ControlledSource();
        var pending = new ControlledSource();
        scheduler.Enqueue(current);
        scheduler.Enqueue(pending);
        Assert.Same(current, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        IAudioSource[] expected = scheduler.GetEditableQueueSnapshot().Queue;

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ReplaceQueueAsync(expected, [pending, pending]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.ReplaceQueueAsync(expected, [current]));

        Assert.Equal([pending], scheduler.GetEditableQueueSnapshot().Queue);
    }

    [Fact]
    public async Task QueueEditCleanupFailureStillDisposesOtherRemovedSources()
    {
        await using var scheduler = new AudioScheduler();
        var failing = new ControlledSource(cleanup: () => throw new IOException("Cleanup failure"));
        var other = new ControlledSource();
        scheduler.Enqueue(failing);
        scheduler.Enqueue(other);

        await Assert.ThrowsAsync<AggregateException>(() => scheduler.ReplaceQueueAsync(scheduler.GetEditableQueueSnapshot().Queue, []));

        Assert.Empty(scheduler.GetEditableQueueSnapshot().Queue);
        Assert.Equal(1, failing.CleanupCalls);
        Assert.Equal(1, other.CleanupCalls);
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

    private sealed class DownloadHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
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
