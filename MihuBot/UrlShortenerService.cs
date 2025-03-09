using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using System.ComponentModel.DataAnnotations.Schema;

namespace MihuBot;

public sealed class UrlShortenerService
{
    private readonly IDbContextFactory<MihuBotDbContext> _db;

    public UrlShortenerService(IDbContextFactory<MihuBotDbContext> db)
    {
        _db = db;
    }

    public async Task<Entry> GetAsync(string id)
    {
        if (!Snowflake.TryGetFromString(id, out ulong snowflake))
        {
            return null;
        }

        await using var context = _db.CreateDbContext();

        Entry entry = await context.UrlShortenerEntries.FindAsync([(long)snowflake]);

        if (entry is not null)
        {
            entry.FetchCount++;
            await context.SaveChangesAsync();
        }

        return entry;
    }

    public async Task<Entry> CreateAsync(string creationSource, Uri originalUrl)
    {
        if (originalUrl.IdnHost.Equals("mihubot.xyz", StringComparison.OrdinalIgnoreCase) &&
            originalUrl.PathAndQuery.StartsWith("/r/", StringComparison.OrdinalIgnoreCase))
        {
            return await GetAsync(originalUrl.PathAndQuery[3..]);
        }

        var entry = new Entry
        {
            Id = (long)Snowflake.Next(),
            CreationSource = creationSource,
            OriginalUrl = originalUrl.AbsoluteUri,
        };

        await using var context = _db.CreateDbContext();

        context.UrlShortenerEntries.Add(entry);

        await context.SaveChangesAsync();

        return entry;
    }

    [Table("urlShortener")]
    public sealed class Entry
    {
        public long Id { get; set; }

        public string CreationSource { get; set; }

        public string OriginalUrl { get; set; }

        public long FetchCount { get; set; }

        public DateTime Timestamp => SnowflakeUtils.FromSnowflake((ulong)Id).UtcDateTime;

        public string ShortUrl => $"https://mihubot.xyz/r/{Snowflake.GetString((ulong)Id)}";
    }
}
