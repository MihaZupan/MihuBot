using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using Newtonsoft.Json;

namespace MihuBot.Reminders;

public sealed class ReminderService
{
    private readonly IDbContextFactory<MihuBotDbContext> _db;
    private readonly Logger _logger;

    public ReminderService(Logger logger, IDbContextFactory<MihuBotDbContext> db)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));

        // TEMPORARY - DB MIGRATION
        if (File.Exists($"{Constants.StateDirectory}/Reminders.json"))
        {
            var entries = JsonConvert.DeserializeObject<List<ReminderEntry>>(File.ReadAllText($"{Constants.StateDirectory}/Reminders.json"));

            var existing = GetAllRemindersAsync().GetAwaiter().GetResult();

            foreach (var e in entries)
            {
                if (existing.Any(r => r.MessageId == e.MessageId))
                {
                    continue;
                }

                ScheduleAsync(e).GetAwaiter().GetResult();
            }
        }
    }

    public async ValueTask<IEnumerable<ReminderEntry>> GetAllRemindersAsync()
    {
        await using var context = _db.CreateDbContext();

        return await context.Reminders.AsNoTracking().ToArrayAsync();
    }

    public async ValueTask<IEnumerable<ReminderEntry>> GetRemindersForUserAsync(ulong userId)
    {
        await using var context = _db.CreateDbContext();

        return await context.Reminders.AsNoTracking().Where(r => r.AuthorId == (long)userId).ToArrayAsync();
    }

    public async ValueTask<ICollection<ReminderEntry>> GetPendingRemindersAsync()
    {
        try
        {
            await using var context = _db.CreateDbContext();
            var now = DateTime.UtcNow;

            List<ReminderEntry> entries = await context.Reminders.Where(r => r.Time < now).ToListAsync();

            foreach (ReminderEntry entry in entries)
            {
                Log($"Popping reminder from the heap {entry}", entry);
            }

            context.Reminders.RemoveRange(entries);

            entries.RemoveAll(r => now - r.Time > TimeSpan.FromMinutes(1));

            await context.SaveChangesAsync();

            return entries;
        }
        catch (Exception ex)
        {
            _logger.DebugLog(ex.ToString());
        }

        return Array.Empty<ReminderEntry>();
    }

    public async Task ScheduleAsync(ReminderEntry entry)
    {
        Log($"Setting reminder entry for {entry}", entry);

        await using var context = _db.CreateDbContext();

        context.Reminders.Add(entry);

        await context.SaveChangesAsync();
    }

    public async ValueTask<bool> RemoveReminderAsync(ReminderEntry entry)
    {
        await using var context = _db.CreateDbContext();

        context.Reminders.Remove(entry);

        return await context.SaveChangesAsync() != 0;
    }

    private void Log(string message, ReminderEntry entry) =>
        _logger.DebugLog(message, entry.GuildId, entry.ChannelId, entry.MessageId, (ulong)entry.AuthorId);
}
