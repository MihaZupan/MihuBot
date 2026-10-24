using System.Text.Json;
using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using MihuBot.DB;
using MihuBot.Discord.Audio;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;

namespace MihuBot.Tests.Discord;

public sealed class AudioAutoplayTests
{
    [Fact]
    public async Task StatusMonitorRefreshesProgressPauseQueueAndTrackChanges()
    {
        await using var scheduler = new AudioScheduler();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var updates = Channel.CreateUnbounded<(Embed Embed, bool NewTrack)>();
        var current = new TestSource(isAutoplay: true)
        {
            Duration = TimeSpan.FromMinutes(4),
            Remaining = TimeSpan.FromSeconds(150)
        };
        scheduler.Enqueue(current);
        Assert.Same(current, await scheduler.GetAudioSourceAsync(cancellation.Token));
        bool paused = false;
        Task monitor = AudioPlayer.RunStatusUpdatesAsync(scheduler, () => Volatile.Read(ref paused), (embed, newTrack) =>
        {
            Assert.True(updates.Writer.TryWrite((embed, newTrack)));
            return Task.FromResult(true);
        }, cancellation.Token);

        try
        {
            var initial = await ReadUpdateAsync();
            Assert.True(initial.NewTrack);
            Assert.Contains("1:30 / 4:00", initial.Embed.Description);
            current.Remaining = TimeSpan.FromSeconds(140);

            var progress = await ReadUpdateAsync();
            Assert.False(progress.NewTrack);
            Assert.Contains("1:40 / 4:00 (2:20 remaining)", progress.Embed.Description);

            Volatile.Write(ref paused, true);
            var pause = await ReadUpdateAsync();
            Assert.Equal("Paused", pause.Embed.Title);
            Assert.False(pause.NewTrack);

            var next = new TestSource(isAutoplay: true) { Description = "Next song" };
            scheduler.Enqueue(next);
            var queue = await ReadUpdateAsync();
            Assert.Equal("1. Next song", Assert.Single(queue.Embed.Fields).Name);
            Assert.False(queue.NewTrack);

            scheduler.SkipCurrent(current);
            Assert.Same(next, await scheduler.GetAudioSourceAsync(cancellation.Token));
            Volatile.Write(ref paused, false);
            var nextTrack = await ReadUpdateAsync();
            Assert.Equal("Now playing", nextTrack.Embed.Title);
            Assert.Contains("Next song", nextTrack.Embed.Description);
            Assert.True(nextTrack.NewTrack);

            scheduler.SkipCurrent(next);
            var empty = await ReadUpdateAsync();
            Assert.Equal("Nothing playing", empty.Embed.Title);
            Assert.Equal("The queue is currently empty", empty.Embed.Footer?.Text);
            Assert.False(empty.NewTrack);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
        }

        async Task<(Embed Embed, bool NewTrack)> ReadUpdateAsync() =>
            await updates.Reader.ReadAsync(cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task StatusMonitorRetriesFailedUpdatesWhilePaused()
    {
        await using var scheduler = new AudioScheduler();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var current = new TestSource(isAutoplay: true);
        scheduler.Enqueue(current);
        Assert.Same(current, await scheduler.GetAudioSourceAsync(cancellation.Token));
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        Task monitor = AudioPlayer.RunStatusUpdatesAsync(scheduler, () => true, (_, _) =>
        {
            if (++attempts == 1)
            {
                return Task.FromResult(false);
            }

            retried.SetResult();
            return Task.FromResult(true);
        }, cancellation.Token);

        try
        {
            await retried.Task.WaitAsync(cancellation.Token);
            Assert.Equal(2, attempts);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
        }
    }

    [Theory]
    [InlineData("Now playing")]
    [InlineData("Queue")]
    public void StatusIncludesCurrentProgressAndQueuedSongs(string title)
    {
        var current = new TestSource(isAutoplay: true)
        {
            Duration = TimeSpan.FromMinutes(4),
            Remaining = TimeSpan.FromSeconds(150)
        };
        var next = new TestSource(isAutoplay: true)
        {
            Description = "Next song",
            Duration = TimeSpan.FromSeconds(90)
        };

        var embed = AudioPlayer.BuildStatusEmbed(title, current, [next]);

        Assert.Contains("1:30 / 4:00 (2:30 remaining)", embed.Description);
        Assert.Contains("AI autoplay", embed.Description);
        var field = Assert.Single(embed.Fields);
        Assert.Equal("1. Next song", field.Name);
        Assert.Contains("1:30", field.Value);
    }

    [Fact]
    public void StatusExplicitlyShowsAnEmptyQueueWithoutACurrentTrack()
    {
        var embed = AudioPlayer.BuildStatusEmbed("Queue", null!, []);

        Assert.Empty(embed.Fields);
        Assert.Equal("The queue is currently empty", embed.Footer?.Text);
    }

    [Theory]
    [InlineData(90123, 1, "25:02:02 / 25:02:03 (0:01 remaining)")]
    [InlineData(90, -1, "1:30 / 1:30 (0:00 remaining)")]
    [InlineData(null, 90, "1:30 remaining (duration unknown)")]
    [InlineData(90, null, "1:30 (remaining unknown)")]
    [InlineData(null, null, "Duration unknown (remaining unknown)")]
    public void StatusHandlesLongFinishedAndUnknownDurations(int? duration, int? remaining, string expected)
    {
        var current = new TestSource(isAutoplay: true)
        {
            Duration = duration is { } total ? TimeSpan.FromSeconds(total) : null,
            Remaining = remaining is { } left ? TimeSpan.FromSeconds(left) : null
        };

        var embed = AudioPlayer.BuildStatusEmbed("Now playing", current, []);

        Assert.Contains(expected, embed.Description);
        Assert.Equal("The queue is currently empty", embed.Footer?.Text);
    }

    [Fact]
    public async Task ManualRequestsInvalidatePreparedAutoplayWithoutInterruptingCurrentTrack()
    {
        await using var scheduler = new AudioScheduler();
        var current = new TestSource();
        var manual = new TestSource();
        var autoplay = new TestSource(isAutoplay: true);
        scheduler.Enqueue(current);
        Assert.Same(current, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));

        scheduler.Enqueue(manual);
        Assert.Same(current, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        Assert.True(autoplay.Disposed);

        scheduler.SkipCurrent(current);
        Assert.Same(manual, await scheduler.GetAudioSourceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ManualRequestsDoNotWaitForAutoplayInitialization()
    {
        await using var scheduler = new AudioScheduler();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var autoplay = new TestSource(isAutoplay: true, initialized: false);
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));
        Task<IAudioSource> next = scheduler.GetAudioSourceAsync(timeout.Token).AsTask();
        Assert.False(next.IsCompleted);

        var manual = new TestSource();
        scheduler.Enqueue(manual);

        Assert.Same(manual, await next);
        Assert.True(autoplay.Disposed);
    }

    [Fact]
    public async Task StaleRecommendationsAreRejectedEvenAfterManualQueueDrains()
    {
        await using var scheduler = new AudioScheduler();
        long revision = scheduler.Revision;
        var manual = new TestSource();
        scheduler.Enqueue(manual);
        Assert.Same(manual, await scheduler.GetAudioSourceAsync(CancellationToken.None));
        scheduler.SkipCurrent(manual);
        var autoplay = new TestSource(isAutoplay: true);

        Assert.False(scheduler.TrySetAutoplay(autoplay, revision));
        Assert.False(scheduler.HasAutoplay);
    }

    [Fact]
    public async Task InvalidatingAutoplayRejectsRecommendationsStillBeingGenerated()
    {
        await using var scheduler = new AudioScheduler();
        long revision = scheduler.Revision;
        await scheduler.ClearAutoplayAsync(invalidate: true);

        Assert.False(scheduler.TrySetAutoplay(new TestSource(isAutoplay: true), revision));
    }

    [Fact]
    public async Task PreparedAutoplayWakesAnEmptyQueueAndIsConsumedOnlyOnce()
    {
        await using var scheduler = new AudioScheduler();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<IAudioSource> next = scheduler.GetAudioSourceAsync(timeout.Token).AsTask();
        var autoplay = new TestSource(isAutoplay: true);
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));
        Assert.Same(autoplay, await next);
        Assert.False(scheduler.HasAutoplay);
        long revision = scheduler.Revision;
        Assert.Same(autoplay, await scheduler.GetAudioSourceAsync(timeout.Token));
        Assert.Equal(revision, scheduler.Revision);

        scheduler.SkipCurrent(autoplay);
        Task<IAudioSource> waiting = scheduler.GetAudioSourceAsync(timeout.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        var manual = new TestSource();
        scheduler.Enqueue(manual);
        Assert.Same(manual, await waiting);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("skip")]
    [InlineData("fail")]
    public async Task ClearedSkippedOrFailedAutoplayCannotBecomeCurrent(string action)
    {
        await using var scheduler = new AudioScheduler();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var autoplay = new TestSource(isAutoplay: true, initialized: false);
        Assert.True(scheduler.TrySetAutoplay(autoplay, scheduler.Revision));
        Task<IAudioSource> next = scheduler.GetAudioSourceAsync(timeout.Token).AsTask();

        if (action == "clear")
        {
            await scheduler.ClearAutoplayAsync();
            autoplay.Initialized.TrySetResult(true);
        }
        else if (action == "skip")
        {
            await scheduler.SkipAsync();
            autoplay.Initialized.TrySetResult(true);
        }
        else
        {
            autoplay.Initialized.SetResult(false);
        }

        var manual = new TestSource();
        scheduler.Enqueue(manual);
        Assert.Same(manual, await next);
        Assert.True(autoplay.Disposed);
    }

    [Fact]
    public async Task DisposingSchedulerWakesAnEmptyQueueAndRejectsLateRecommendations()
    {
        var scheduler = new AudioScheduler();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<IAudioSource> next = scheduler.GetAudioSourceAsync(timeout.Token).AsTask();
        await scheduler.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => next);
        Assert.False(scheduler.TrySetAutoplay(new TestSource(isAutoplay: true), scheduler.Revision));
    }

    [Fact]
    public async Task PlaybackHistoryMigrationPreservesRemindersAndLocationsAfterNamespaceChanges()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new MihuBotDbContext(
            new DbContextOptionsBuilder<MihuBotDbContext>().UseSqlite(connection).Options);
        await context.GetService<IMigrator>().MigrateAsync("20260802233141_CoreRootZStandard");
        context.Reminders.Add(new()
        {
            GuildId = 42,
            AuthorId = 43,
            Message = "Existing reminder",
            Time = new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            RepeatYearly = true
        });
        context.UserLocation.Add(new()
        {
            UserId = 43,
            Name = "Ljubljana",
            Latitude = 46.05,
            Longitude = 14.5
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.MigrateAsync();

        var reminder = Assert.Single(await context.Reminders.AsNoTracking().ToArrayAsync());
        Assert.Equal("Existing reminder", reminder.Message);
        Assert.Equal(42UL, reminder.GuildId);
        Assert.Equal(43, reminder.AuthorId);
        Assert.True(reminder.RepeatYearly);
        var location = Assert.Single(await context.UserLocation.AsNoTracking().ToArrayAsync());
        Assert.Equal(43, location.UserId);
        Assert.Equal("Ljubljana", location.Name);
        Assert.Equal(46.05, location.Latitude);
        Assert.Equal(14.5, location.Longitude);
        Assert.Empty(await context.AudioPlaybackHistory.ToArrayAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task PlaybackHistoryMigratesAndRetainsAllGuildEntriesWhileReturningOnlyLatest100()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new HistoryFactory(new DbContextOptionsBuilder<MihuBotDbContext>().UseSqlite(connection).Options);

        await using (var context = factory.CreateDbContext())
        {
            await context.Database.MigrateAsync();
            Assert.False(context.Database.HasPendingModelChanges());
            context.AudioPlaybackHistory.AddRange(Enumerable.Range(0, 125).Select(i => new AudioPlaybackEntry
            {
                GuildId = 42,
                Title = $"Song {i}",
                Url = $"https://example.com/{i}",
                StartedAt = DateTime.UtcNow,
                Outcome = AudioPlaybackOutcome.Completed
            }));
            context.AudioPlaybackHistory.Add(new() { GuildId = 43, Title = "Other guild", Url = "other" });
            await context.SaveChangesAsync();
        }

        var history = new AudioHistoryService(factory);
        AudioPlaybackEntry entry = await history.StartAsync(42, new TestSource(isAutoplay: true));
        await history.FinishAsync(entry, AudioPlaybackOutcome.Skipped);
        AudioPlaybackEntry[] recent = await new AudioHistoryService(factory).GetRecentAsync(42, CancellationToken.None);

        Assert.Equal(100, recent.Length);
        Assert.Equal("Song 26", recent[0].Title);
        Assert.Equal(entry.Id, recent[^1].Id);
        Assert.True(recent[^1].IsAutoplay);
        Assert.Equal(AudioPlaybackOutcome.Skipped, recent[^1].Outcome);
        Assert.NotNull(recent[^1].EndedAt);
        Assert.Null(recent[^1].RequesterId);
        Assert.All(recent, e => Assert.Equal(42UL, e.GuildId));

        await using var verification = factory.CreateDbContext();
        Assert.Equal(126, await verification.AudioPlaybackHistory.CountAsync(e => e.GuildId == 42));
        Assert.Single(await history.GetRecentAsync(43, CancellationToken.None));
    }

    [Fact]
    public async Task PromptUsesLatest100TracksInOrderWithoutRequesterIds()
    {
        AudioPlaybackEntry[] entries = Enumerable.Range(0, 125).Select(i => new AudioPlaybackEntry
        {
            Title = $"Song {i}",
            Url = $"https://example.com/{i}",
            RequesterId = 123456789,
            IsAutoplay = i % 2 == 0,
            Outcome = AudioPlaybackOutcome.Skipped
        }).ToArray();
        using var timeout = new CancellationTokenSource();
        ChatOptions? sentOptions = null;
        using var chat = new TestChatClient((messages, options, token) =>
        {
            sentOptions = options;
            ChatMessage[] sent = messages.ToArray();
            Assert.Equal(2, sent.Length);
            Assert.Equal(ChatRole.System, sent[0].Role);
            Assert.Contains("manually requested", sent[0].Text, StringComparison.Ordinal);
            Assert.Equal(ChatRole.User, sent[1].Role);
            using var data = JsonDocument.Parse(sent[1].Text!);
            Assert.Equal(100, data.RootElement.GetArrayLength());
            Assert.Equal("Song 25", data.RootElement[0].GetProperty("Title").GetString());
            Assert.Equal("Song 124", data.RootElement[99].GetProperty("Title").GetString());
            Assert.Equal("Skipped", data.RootElement[99].GetProperty("Outcome").GetString());
            Assert.DoesNotContain("RequesterId", sent[1].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("123456789", sent[1].Text, StringComparison.Ordinal);
            Assert.IsType<ChatResponseFormatJson>(options?.ResponseFormat);
            Assert.Equal(timeout.Token, token);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"Songs":[{"Artist":"Artist","Title":"Next song"}]}""")));
        });

        SongSuggestion[] suggestions = await AudioRecommendationService.SuggestAsync(chat, entries, timeout.Token);
        Assert.Equal(new SongSuggestion("Artist", "Next song"), Assert.Single(suggestions));
        Assert.NotNull(sentOptions);
        Assert.Equal(16_384, sentOptions.MaxOutputTokens);
        var rawOptions = Assert.IsType<OpenAI.Chat.ChatCompletionOptions>(sentOptions.RawRepresentationFactory?.Invoke(chat));
#pragma warning disable OPENAI001
        Assert.Equal(OpenAI.Chat.ChatReasoningEffortLevel.High, rawOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData("""{"Songs":[]}""")]
    [InlineData("""{"Songs":null}""")]
    [InlineData("""{"Songs":[null]}""")]
    [InlineData("""{"Songs":[{"Artist":"","Title":"Song"}]}""")]
    [InlineData("""{"Songs":[{"Artist":"Artist","Title":" "}]}""")]
    public async Task InvalidSuggestionsFailExplicitly(string response)
    {
        using var chat = new TestChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AudioRecommendationService.SuggestAsync(chat, [], CancellationToken.None));
    }

    [Fact]
    public async Task MissingAIProducesAnExplicitUnavailableError()
    {
        var service = new AudioRecommendationService(null!, []);

        Assert.False(service.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecommendAsync(42, CancellationToken.None));
    }

    [Theory]
    [InlineData("Artist - A Song (Official Audio)", "Artist", "A Song", true)]
    [InlineData("A-Song", "Artist", "A Song", true)]
    [InlineData("AC/DC - Thunderstruck", "ACDC", "Thunderstruck", true)]
    [InlineData("An unrelated song", "Artist", "Missing song", false)]
    [InlineData("Someone else - A Song", "Missing artist", "A Song", false)]
    public void SearchResultsMustMatchTheSuggestedSong(string videoTitle, string artist, string title, bool expected)
    {
        Assert.Equal(expected, AudioRecommendationService.MatchesSuggestion(CreateVideo(videoTitle, 180), new(artist, title)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(900, true)]
    [InlineData(901, false)]
    public void RecommendationsRejectUnknownOrUnsuitableDuration(int? seconds, bool expected)
    {
        Video video = CreateVideo("New song", seconds);
        Assert.Equal(expected, AudioRecommendationService.IsSuitable(video, []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecommendationsRejectRecentVideoIdsOrTitles(bool sameUrl)
    {
        Video video = CreateVideo("A Song", 180);
        var recent = new AudioPlaybackEntry
        {
            Url = sameUrl ? video.Url : "another upload",
            Title = sameUrl ? "different title" : "a song"
        };

        Assert.False(AudioRecommendationService.IsSuitable(video, [recent]));
    }

    [Theory]
    [InlineData("Official Music Video")]
    [InlineData("OFFICIAL VIDEO")]
    [InlineData("Video Clip")]
    public void RecommendationsPreferShorterAudioOverMusicVideos(string label)
    {
        Video musicVideo = CreateVideo($"Artist - A Song ({label})", 240);
        Video audio = CreateVideo("Artist - A Song (Official Audio)", 200);

        Assert.Same(audio, AudioRecommendationService.SelectVideo([musicVideo, audio], new("Artist", "A Song"), []));
    }

    [Fact]
    public void RecommendationsPreferAlternativesFromTheSameChannel()
    {
        Video musicVideo = CreateVideo("Artist - A Song (Music Video)", 240);
        Video otherUpload = CreateVideo("Artist - A Song (Audio)", 200, "UC1123456789012345678901");
        Video officialAudio = CreateVideo("Artist - A Song (Official Audio)", 210);

        Assert.Same(officialAudio, AudioRecommendationService.SelectVideo(
            [musicVideo, otherUpload, officialAudio], new("Artist", "A Song"), []));
    }

    [Theory]
    [InlineData("Artist - A Song (Audio)", 179)]
    [InlineData("Artist - A Song (Audio)", 241)]
    [InlineData("Artist - Another Song (Audio)", 200)]
    [InlineData("Artist - A Song (Official Video)", 200)]
    public void RecommendationsKeepMusicVideoWhenThereIsNoComparableAudio(string title, int seconds)
    {
        Video musicVideo = CreateVideo("Artist - A Song (Music Video)", 240);
        Video alternative = CreateVideo(title, seconds);

        Assert.Same(musicVideo, AudioRecommendationService.SelectVideo(
            [musicVideo, alternative], new("Artist", "A Song"), []));
    }

    [Fact]
    public void RecommendationsDoNotReorderAudioResultsOrSelectRecentAlternatives()
    {
        Video first = CreateVideo("Artist - A Song (Audio)", 240);
        Video shorter = CreateVideo("Artist - A Song (Official Audio)", 200);
        Assert.Same(first, AudioRecommendationService.SelectVideo([first, shorter], new("Artist", "A Song"), []));

        Video musicVideo = CreateVideo("Artist - A Song (Music Video)", 240);
        AudioPlaybackEntry[] recent = [new() { Title = shorter.Title, Url = "another upload" }];
        Assert.Same(musicVideo, AudioRecommendationService.SelectVideo(
            [musicVideo, shorter], new("Artist", "A Song"), recent));
        Assert.Null(AudioRecommendationService.SelectVideo([shorter], new("Artist", "A Song"), recent));
    }

    private static Video CreateVideo(string title, int? seconds, string channelId = "UC0123456789012345678901") =>
        new("afqQwmXTcPA", title, new Author(channelId, "Artist"), DateTimeOffset.UtcNow,
            "", seconds is { } value ? TimeSpan.FromSeconds(value) : null, [], [], new Engagement(0, 0, 0));

    private sealed class HistoryFactory(DbContextOptions<MihuBotDbContext> options) : IDbContextFactory<MihuBotDbContext>
    {
        public MihuBotDbContext CreateDbContext() => new(options);
    }

    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            respond(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestSource : IAudioSource
    {
        public TaskCompletionSource<bool> Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public bool IsAutoplay { get; }
        public SocketGuildUser Requester => null!;
        public string Description { get; init; } = "Test song";
        public string Url => "https://www.youtube.com/watch?v=afqQwmXTcPA";
        public string ThumbnailUrl => "";
        public TimeSpan? Remaining { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan? Duration { get; init; } = TimeSpan.FromSeconds(10);

        public TestSource(bool isAutoplay = false, bool initialized = true)
        {
            IsAutoplay = isAutoplay;

            if (initialized)
            {
                Initialized.SetResult(true);
            }
        }

        public void StartInitializing(int bitrateHintKbit) { }
        public Task<bool> EnsureInitializedAsync() => Initialized.Task;
        public ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
