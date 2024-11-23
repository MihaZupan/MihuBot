using Microsoft.EntityFrameworkCore;
using MihuBot.Reminders;

namespace MihuBot.DB;

public sealed class MihuBotDbContext : DbContext
{
    public MihuBotDbContext(DbContextOptions<MihuBotDbContext> options) : base(options)
    { }

    public DbSet<ReminderEntry> Reminders { get; set; }
}
