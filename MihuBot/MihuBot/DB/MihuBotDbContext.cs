using Microsoft.EntityFrameworkCore;
using MihuBot.Reminders;
using System.ComponentModel.DataAnnotations.Schema;

namespace MihuBot.DB;

public sealed class MihuBotDbContext : DbContext
{
    public MihuBotDbContext(DbContextOptions<MihuBotDbContext> options) : base(options)
    { }

    public DbSet<DeDuplicationEntry> DeDuplication { get; set; }

    public DbSet<ReminderEntry> Reminders { get; set; }
}

[Table("deduplication")]
public sealed class DeDuplicationEntry
{
    public string Id { get; set; }
}

public static class MihuBotDbExtensions
{
    public static async Task<bool> ContainsDeDupeAsync(this IDbContextFactory<MihuBotDbContext> factory, string key)
    {
        await using var context = factory.CreateDbContext();

        return await context.DeDuplication.AsNoTracking().AnyAsync(e => e.Id == key);
    }

    public static async Task<bool> TryDeDupeAsync(this IDbContextFactory<MihuBotDbContext> factory, string key)
    {
        await using var context = factory.CreateDbContext();

        context.DeDuplication.Add(new DeDuplicationEntry { Id = key });

        return await context.SaveChangesAsync() != 0;
    }
}