using Microsoft.EntityFrameworkCore;
using MihuBot.Reminders;
using MihuBot.RuntimeUtils;

namespace MihuBot.DB;

public sealed class MihuBotDbContext : DbContext
{
    public MihuBotDbContext(DbContextOptions<MihuBotDbContext> options) : base(options)
    { }

    public DbSet<ReminderEntry> Reminders { get; set; }

    public DbSet<CompletedJobDbEntry> CompletedJobs { get; set; }

    public DbSet<UrlShortenerService.Entry> UrlShortenerEntries { get; set; }
}
