using System.Text.Json;
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
        var service = new AudioRecommendationService(null!, null!, []);

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

    private static Video CreateVideo(string title, int? seconds) =>
        new("afqQwmXTcPA", title, new Author("UC0123456789012345678901", "Artist"), DateTimeOffset.UtcNow,
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
        public string Description => "Test song";
        public string Url => "https://www.youtube.com/watch?v=afqQwmXTcPA";
        public string ThumbnailUrl => "";
        public TimeSpan? Remaining => TimeSpan.FromSeconds(10);

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
