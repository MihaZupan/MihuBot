using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.Commands;
using MihuBot.DB;
using MihuBot.Discord.Location;
using MihuBot.Discord.Reminders;

namespace MihuBot.Tests.Discord;

public sealed class ReminderRequestParserTests
{
    private static readonly DateTime s_now = new(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("remind me to do Foo in 2 hours", 7200)]
    [InlineData("!remind me in 197 minutes to play starfield", 11820)]
    [InlineData("remind me in an hour", 3600)]
    [InlineData("REMIND ME IN 1,5 HOURS", 5400)]
    [InlineData("remind me in 30s", 30)]
    [InlineData("remind me in .5 hours", 1800)]
    [InlineData("remind me in a day", 86400)]
    [InlineData("remind me in a week", 604800)]
    [InlineData("remind me in 2 hours.", 7200)]
    [InlineData("remind me to do Foo in 2 hours!", 7200)]
    public void SimpleRequestsUseTheExistingDurationParser(string request, int seconds)
    {
        Assert.True(ReminderCommand.TryParseSimpleRemindTime(request, s_now, out DateTime time));
        Assert.Equal(s_now.AddSeconds(seconds), time);
        Assert.Equal(DateTimeKind.Utc, time.Kind);
    }

    [Theory]
    [InlineData("remind me in 2 months", "2026-10-12T21:00:00Z", "2026-12-12T21:00:00Z")]
    [InlineData("remind me in 2y", "2026-10-12T21:00:00Z", "2028-10-12T21:00:00Z")]
    [InlineData("remind me in 2 months", "2026-12-31T21:00:00Z", "2027-02-28T21:00:00Z")]
    [InlineData("remind me in a year", "2028-02-29T21:00:00Z", "2029-02-28T21:00:00Z")]
    public void MonthAndYearRequestsUseExistingCalendarArithmetic(string request, string now, string expected)
    {
        DateTime start = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture).UtcDateTime;
        DateTime end = DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture).UtcDateTime;

        Assert.True(ReminderCommand.TryParseSimpleRemindTime(request, start, out DateTime time));
        Assert.Equal(end, time);
        Assert.Equal(DateTimeKind.Utc, time.Kind);
    }

    [Theory]
    [InlineData("remind me to run for 2 hours in 3 hours")]
    [InlineData("remind me in 2 hours and 30 minutes")]
    [InlineData("remind me in a day or in a year")]
    [InlineData("remind me on Dec 1st")]
    [InlineData("remind me tomorrow at 9")]
    [InlineData("remind me in 2 hours to meet at 5")]
    [InlineData("remind me to play for 2 hours")]
    [InlineData("remind me in 2 hours before the event")]
    [InlineData("remind me in 2 hours to play in the VC")]
    [InlineData("remind me on Dec 1st in 2 hours")]
    [InlineData("remind me in 2 hours to exercise daily")]
    [InlineData("remind me in 0 hours")]
    [InlineData("remind me in -2 hours")]
    [InlineData("remind me in roughly 2 hours")]
    public void ComplexOrInvalidRequestsDoNotSilentlyUseAPartialDuration(string request)
    {
        Assert.False(ReminderCommand.TryParseSimpleRemindTime(request, s_now, out _));
    }

    [Theory]
    [InlineData("2 hours and 30 minutes", 9000)]
    [InlineData("1 day, 2 hours", 93600)]
    [InlineData("an hour", 3600)]
    [InlineData("0.5 hours", 1800)]
    [InlineData("a week", 604800)]
    public void ExistingCompoundDurationParsingIsPreserved(string duration, int seconds)
    {
        Assert.True(ReminderCommand.TryParseRemindTimeCore(duration, s_now, out DateTime time));
        Assert.Equal(s_now.AddSeconds(seconds), time);
    }

    [Theory]
    [InlineData(9000)]
    [InlineData(10800)]
    public void RelativePredictionsProduceUtcTimes(double seconds)
    {
        var result = ReminderRequestParser.ValidatePrediction(new(seconds, null, null, null), s_now);

        Assert.Equal(s_now.AddSeconds(seconds), result.Time);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("2026-12-01T09:00:00", "Europe/Ljubljana", 2026, 12, 1, 8)]
    [InlineData("2027-07-01T09:00:00", "Europe/Ljubljana", 2027, 7, 1, 7)]
    [InlineData("2026-12-01T09:00:00", "America/New_York", 2026, 12, 1, 14)]
    [InlineData("2026-12-01T09:00:00", "UTC", 2026, 12, 1, 9)]
    public void CalendarPredictionsUseTimezoneRulesAtTheScheduledDate(
        string localTime, string zone, int year, int month, int day, int hour)
    {
        var result = ReminderRequestParser.ValidatePrediction(new(null, localTime, zone, null), s_now);

        Assert.Equal(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc), result.Time);
        Assert.Equal(DateTimeKind.Utc, result.Time?.Kind);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("2027-03-28T02:30:00", "Europe/Ljubljana")]
    [InlineData("2026-10-25T02:30:00", "Europe/Ljubljana")]
    [InlineData("2027-03-14T02:30:00", "America/New_York")]
    [InlineData("2026-11-01T01:30:00", "America/New_York")]
    public void SkippedOrRepeatedLocalTimesRequireClarification(string localTime, string zone)
    {
        var result = ReminderRequestParser.ValidatePrediction(new(null, localTime, zone, null), s_now);

        Assert.Null(result.Time);
        Assert.Contains("daylight saving", result.Error);
    }

    public static TheoryData<double?, string?, string?, string?> InvalidPredictions => new()
    {
        { null, null, null, null },
        { 0, null, null, null },
        { -1, null, null, null },
        { double.NaN, null, null, null },
        { double.PositiveInfinity, null, null, null },
        { double.MaxValue, null, null, null },
        { 60, "2026-12-01T09:00:00", "UTC", null },
        { 60, null, "UTC", null },
        { 60, null, null, "Which time?" },
        { null, "2026-12-01T09:00:00", null, null },
        { null, "2026-12-01T09:00:00", "Not/AZone", null },
        { null, "2026-12-01", "UTC", null },
        { null, "2026-12-01T09:00:00Z", "UTC", null },
        { null, "2026-12-01T09:00:00+01:00", "UTC", null },
        { null, "2026-02-30T09:00:00", "UTC", null },
        { null, "2026-10-12T21:00:00", "UTC", null },
        { null, "2026-10-12T20:00:00", "UTC", null },
        { null, "3001-01-01T09:00:00", "UTC", null },
    };

    [Theory]
    [MemberData(nameof(InvalidPredictions))]
    public void InvalidPredictionsCannotScheduleAReminder(double? seconds, string? localTime, string? zone, string? clarification)
    {
        var result = ReminderRequestParser.ValidatePrediction(new(seconds, localTime, zone, clarification), s_now);

        Assert.Null(result.Time);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void AmbiguityIsReturnedAsClarificationRatherThanScheduled()
    {
        var result = ReminderRequestParser.ValidatePrediction(new(null, null, null, "In 2 hours or in 3 hours?"), s_now);

        Assert.Null(result.Time);
        Assert.Equal("In 2 hours or in 3 hours?", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StructuredRequestsIncludeCurrentTimeAndOnlyTheSavedLocation(bool hasLocation)
    {
        LocationService.UserLocation? location = hasLocation
            ? new() { UserId = 42, Name = "Ljubljana", Latitude = 46.05, Longitude = 14.5 }
            : null;
        using var cancellation = new CancellationTokenSource();
        using var chat = new TestChatClient((messages, options, token) =>
        {
            ChatMessage[] sent = messages.ToArray();
            Assert.Equal(2, sent.Length);
            Assert.Equal(ChatRole.System, sent[0].Role);
            Assert.Contains("2026-10-12T21:00:00.0000000Z", sent[0].Text, StringComparison.Ordinal);
            Assert.Contains(hasLocation ? "\"Latitude\":46.05" : "location (may be null): null", sent[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("UserId", sent[0].Text, StringComparison.Ordinal);
            Assert.Contains("09:00", sent[0].Text, StringComparison.Ordinal);
            Assert.Contains("Never assume UTC", sent[0].Text, StringComparison.Ordinal);
            Assert.Equal(ChatRole.User, sent[1].Role);
            Assert.Equal("remind me on Dec 1st", sent[1].Text);
            Assert.IsType<ChatResponseFormatJson>(options?.ResponseFormat);
            Assert.Equal(cancellation.Token, token);

            var prediction = hasLocation
                ? new ReminderPrediction(null, "2026-12-01T09:00:00", "Europe/Ljubljana", null)
                : new ReminderPrediction(null, null, null, "What timezone should I use?");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(prediction))));
        });

        var result = await ReminderRequestParser.ParseWithAIAsync(chat, "remind me on Dec 1st", s_now, location, cancellation.Token);

        if (hasLocation)
        {
            Assert.Equal(new DateTime(2026, 12, 1, 8, 0, 0, DateTimeKind.Utc), result.Time);
        }
        else
        {
            Assert.Null(result.Time);
            Assert.Equal("What timezone should I use?", result.Error);
        }
    }

    [Fact]
    public async Task MalformedResponsesFailInsteadOfScheduling()
    {
        using var chat = new TestChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not JSON"))));

        await Assert.ThrowsAsync<JsonException>(() =>
            ReminderRequestParser.ParseWithAIAsync(chat, "remind me on Dec 1st", s_now, null, CancellationToken.None));
    }

    [Fact]
    public async Task ParserRemainsAvailableWithoutAIOrWeather()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MihuBotDbContext>();
        services.AddSingleton<ReminderRequestParser>();
        await using var provider = services.BuildServiceProvider();

        var parser = provider.GetRequiredService<ReminderRequestParser>();
        var result = await parser.ParseAsync(123, 42, "remind me on Dec 1st", s_now, CancellationToken.None);

        Assert.Null(result.Time);
        Assert.Contains("simple durations", result.Error);
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
}
