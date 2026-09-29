using Microsoft.EntityFrameworkCore;
using Discord;
using MihuBot.DB;
using MihuBot.Agents;
using System.Text.Json;

namespace MihuBot.Tests.Agents;

public sealed class AgentDiscordToolsTests
{
    [Fact]
    public async Task HistoryQueriesLatestThousandMessagesIncludingAllTheirEvents()
    {
        await using var db = new LogsDbContext(new DbContextOptionsBuilder<LogsDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        for (int id = 1; id <= 1005; id++)
        {
            db.Logs.Add(Entry(id, Logger.EventType.MessageReceived, $"Message {id}"));
        }

        db.Logs.AddRange(
            Entry(1000, Logger.EventType.MessageUpdated, "Edited"),
            Entry(1000, Logger.EventType.FileReceived, extra: """{"Id":123,"Filename":"chart.png","Url":"https://example.com/chart.png"}"""),
            Entry(1000, Logger.EventType.MessageDeleted),
            Entry(1004, Logger.EventType.MessageUpdated, ""),
            Entry(1005, Logger.EventType.DebugMessage, "Not a message"),
            Entry(1100, Logger.EventType.MessageDeleted),
            Entry(1101, Logger.EventType.MessageReceived, "Other channel", channel: 3),
            Entry(1102, Logger.EventType.MessageReceived, "Other guild", guild: 4));
        await db.SaveChangesAsync();

        var entries = await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 2000, 1000).ToArrayAsync();
        using var history = JsonDocument.Parse(AgentDiscordTools.BuildHistory(entries, id => $"User {id}"));
        var messages = history.RootElement.EnumerateArray().ToArray();
        Assert.Equal(1000, messages.Length);
        Assert.Equal("6", messages[0].GetProperty("Id").GetString());
        Assert.Equal("1005", messages[^1].GetProperty("Id").GetString());
        JsonElement edited = messages.Single(m => m.GetProperty("Id").GetString() == "1000");
        Assert.Equal("Edited", edited.GetProperty("Content").GetString());
        Assert.Equal("User 42", edited.GetProperty("Author").GetString());
        Assert.True(edited.GetProperty("Edited").GetBoolean());
        Assert.True(edited.GetProperty("Deleted").GetBoolean());
        Assert.Equal("chart.png", edited.GetProperty("Attachments")[0].GetProperty("Filename").GetString());
        Assert.Equal("", messages.Single(m => m.GetProperty("Id").GetString() == "1004").GetProperty("Content").GetString());

        var page = await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 6, 1000).ToArrayAsync();
        Assert.Equal(5, page.Length);
        Assert.Equal([1L, 2, 3, 4, 5], page.Select(e => e.Snowflake));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void HistoryRejectsOutOfRangeLimits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentDiscordTools.QueryHistory(Array.Empty<LogDbEntry>().AsQueryable(), 1, 2, 10, limit));
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData("50", 50)]
    [InlineData("200", 100)]
    public void HistoryCursorExcludesRequestAndLaterMessages(string? cursor, long expected)
    {
        Assert.Equal(expected, AgentDiscordTools.GetHistoryCursor(cursor!, 100));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("18446744073709551615")]
    public void HistoryRejectsInvalidCursors(string cursor)
    {
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.GetHistoryCursor(cursor, 100));
    }

    [Fact]
    public void UploadAcceptsWorkspaceFilesAndRejectsEscapesAndOversizedFiles()
    {
        string directory = Directory.CreateTempSubdirectory("mihubot-upload-tests-").FullName;

        try
        {
            string work = Path.Combine(directory, "work");
            Directory.CreateDirectory(work);
            string file = Path.Combine(work, "plot.png");
            File.WriteAllBytes(file, [1, 2, 3]);
            File.WriteAllText(Path.Combine(directory, "outside.txt"), "not an artifact");

            using (var upload = AgentDiscordTools.OpenUpload(work, "plot.png", 3))
            {
                Assert.Equal(file, upload.Name);
                Assert.Equal(3, upload.Length);
            }

            using (var upload = AgentDiscordTools.OpenUpload(work, file, 3))
            {
                Assert.Equal(file, upload.Name);
            }

            Assert.Throws<ArgumentException>(() => AgentDiscordTools.OpenUpload(work, "plot.png", 2));
            Assert.Throws<ArgumentException>(() => AgentDiscordTools.OpenUpload(work, Path.Combine("..", "outside.txt"), 100));
            Assert.Throws<ArgumentException>(() => AgentDiscordTools.OpenUpload(work, Path.Combine(directory, "work-other", "file"), 100));
            Assert.Throws<FileNotFoundException>(() => AgentDiscordTools.OpenUpload(work, "missing.png", 100));
            Assert.Throws<ArgumentException>(() => AgentDiscordTools.OpenUpload(work, "", 100));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SearchFiltersBeforePaginationAndMatchesLatestContent()
    {
        await using var db = new LogsDbContext(new DbContextOptionsBuilder<LogsDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.Logs.AddRange(
            Entry(1, Logger.EventType.MessageReceived, "Needle original", author: 7),
            Entry(1, Logger.EventType.MessageUpdated, "No longer matches", author: 7),
            Entry(2, Logger.EventType.MessageReceived, "Old content", author: 7),
            Entry(2, Logger.EventType.MessageUpdated, "nEeDlE current", author: 7),
            Entry(2, Logger.EventType.FileReceived, extra: """{"Id":123,"Filename":"chart.png"}""", author: 7),
            Entry(2, Logger.EventType.MessageDeleted),
            Entry(3, Logger.EventType.MessageReceived, "needle other author", author: 8),
            Entry(4, Logger.EventType.FileReceived, extra: """{"Id":124,"Filename":"only-file.png"}""", author: 7),
            Entry(5, Logger.EventType.MessageReceived, @"needle 100%_done \ path", author: 7),
            Entry(6, Logger.EventType.MessageReceived, "needle newest", author: 7),
            Entry(7, Logger.EventType.MessageDeleted),
            Entry(8, Logger.EventType.MessageReceived, "needle other channel", channel: 3, author: 7),
            Entry(9, Logger.EventType.MessageReceived, "needle other guild", guild: 4, author: 7),
            Entry(10, Logger.EventType.MessageReceived, "needle", author: 7),
            Entry(10, Logger.EventType.MessageUpdated, "", author: 7));

        for (int i = 20; i < 200; i++)
        {
            db.Logs.Add(Entry(i, Logger.EventType.MessageReceived, "Newer nonmatching message", author: 7));
        }

        await db.SaveChangesAsync();
        var filter = new AgentDiscordTools.MessageSearch(AuthorId: 7, Text: "NEEDLE");

        var page = await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 1000, 2, filter).ToArrayAsync();
        Assert.Equal([5L, 6], page.Select(e => e.Snowflake).Distinct());

        var older = await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 5, 2, filter).ToArrayAsync();
        Assert.Equal(4, older.Length);
        Assert.All(older, e => Assert.Equal(2, e.Snowflake));
        using var json = JsonDocument.Parse(AgentDiscordTools.BuildHistory(older, id => $"User {id}"));
        var match = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("nEeDlE current", match.GetProperty("Content").GetString());
        Assert.Equal("7", match.GetProperty("AuthorId").GetString());
        Assert.True(match.GetProperty("Deleted").GetBoolean());
        Assert.Single(match.GetProperty("Attachments").EnumerateArray());

        var withAttachments = filter with { HasAttachments = true, After = 2 };
        Assert.Equal(4, await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 1000, 100, withAttachments).CountAsync());
        Assert.Empty(await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 1000, 100,
            withAttachments with { IncludeDeleted = false }).ToArrayAsync());

        var attachmentOnly = filter with { Text = null, HasAttachments = true, IncludeDeleted = false, After = 3 };
        Assert.Equal(4, (await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 1000, 100, attachmentOnly).SingleAsync()).Snowflake);

        var noAttachments = await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 1000, 100,
            filter with { HasAttachments = false }).Select(e => e.Snowflake).ToArrayAsync();
        Assert.Equal([5L, 6], noAttachments);

        var literal = filter with { Text = @"%_done \" };
        Assert.Equal(5, (await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 1000, 100, literal).SingleAsync()).Snowflake);
        Assert.Empty(await AgentDiscordTools.QueryHistory(db.Logs, 1, 2, 1000, 100,
            filter with { Text = "' OR 1=1 --" }).ToArrayAsync());
    }

    [Fact]
    public void SearchBoundsUseUtcAndRespectDateAndMessageCursors()
    {
        var day1 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        ulong requestId = SnowflakeUtils.ToSnowflake(day2.AddDays(1));
        var bounds = AgentDiscordTools.GetSearchBounds("2026-09-01", "2026-09-02T02:00:00+02:00", null!, requestId);
        Assert.Equal((long)SnowflakeUtils.ToSnowflake(day1), bounds.After);
        Assert.Equal((long)SnowflakeUtils.ToSnowflake(day2), bounds.Before);

        string cursor = SnowflakeUtils.ToSnowflake(day1.AddHours(12)).ToString();
        Assert.Equal(long.Parse(cursor), AgentDiscordTools.GetSearchBounds("2026-09-01", "2026-09-02", cursor, requestId).Before);
        Assert.Equal((long)requestId, AgentDiscordTools.GetSearchBounds(null!, "2027-01-01", null!, requestId).Before);
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.GetSearchBounds("2026-09-02", "2026-09-01", null!, requestId));
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.GetSearchBounds("2027-01-01", null!, null!, requestId));
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.GetSearchBounds("not a date", null!, null!, requestId));
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.GetSearchBounds(null!, "9999-01-01", null!, requestId));
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.GetSearchBounds("2010-01-01", null!, null!, requestId));
    }

    [Fact]
    public void UserLookupMatchesAllNamesAndRanksExactMatchesFirst()
    {
        AgentDiscordTools.DiscordUser[] users =
        [
            new("1", "alexander", "Alexander", "alex", false),
            new("2", "alex", "Alice", null!, false),
            new("3", "bob", "Alex", "alias", true),
            new("4", "sarah", "Sarah", "Alexx", false),
            new("5", "ziga", "Žiga", null!, false)
        ];

        Assert.Equal(["2", "1", "3", "4"], AgentDiscordTools.FindUsers(users, "ALEX", 100).Select(u => u.Id));
        Assert.Equal(["2", "1"], AgentDiscordTools.FindUsers(users, "alex", 2).Select(u => u.Id));
        Assert.Equal("3", Assert.Single(AgentDiscordTools.FindUsers(users, "<@!3>", 100)).Id);
        Assert.Equal("2", Assert.Single(AgentDiscordTools.FindUsers(users, "<@2>", 100)).Id);
        Assert.Equal("5", Assert.Single(AgentDiscordTools.FindUsers(users, "žig", 100)).Id);
        Assert.Equal("4", Assert.Single(AgentDiscordTools.FindUsers(users, "4", 100)).Id);
        Assert.Empty(AgentDiscordTools.FindUsers(users, "nobody", 100));
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.FindUsers(users, " ", 100));
        Assert.Throws<ArgumentException>(() => AgentDiscordTools.FindUsers(users, "<@!>", 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentDiscordTools.FindUsers(users, "alex", 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentDiscordTools.FindUsers(users, "alex", 0));
    }

    private static LogDbEntry Entry(long id, Logger.EventType type, string? content = null, string? extra = null, long guild = 1, long channel = 2, long author = 42) => new()
    {
        Snowflake = id, GuildId = guild, ChannelId = channel,
        UserId = type == Logger.EventType.MessageDeleted ? 0 : author,
        Type = type, Content = content, ExtraContentJson = extra
    };
}
