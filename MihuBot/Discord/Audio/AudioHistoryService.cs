using Microsoft.EntityFrameworkCore;
using MihuBot.DB;

namespace MihuBot.Discord.Audio;

public sealed class AudioHistoryService(IDbContextFactory<MihuBotDbContext> db)
{
    public async Task<AudioPlaybackEntry> StartAsync(ulong guildId, IAudioSource source)
    {
        var entry = new AudioPlaybackEntry
        {
            GuildId = guildId,
            RequesterId = source.Requester?.Id,
            Title = source.Description,
            Url = source.Url,
            IsAutoplay = source.IsAutoplay,
            StartedAt = DateTime.UtcNow
        };

        await using var context = await db.CreateDbContextAsync();
        context.AudioPlaybackHistory.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    public async Task FinishAsync(AudioPlaybackEntry entry, AudioPlaybackOutcome outcome)
    {
        await using var context = await db.CreateDbContextAsync();
        entry.EndedAt = DateTime.UtcNow;
        entry.Outcome = outcome;
        context.AudioPlaybackHistory.Update(entry);
        await context.SaveChangesAsync();
    }

    public async Task<AudioPlaybackEntry[]> GetRecentAsync(ulong guildId, CancellationToken cancellationToken)
    {
        await using var context = await db.CreateDbContextAsync(cancellationToken);
        AudioPlaybackEntry[] entries = await context.AudioPlaybackHistory.AsNoTracking()
            .Where(e => e.GuildId == guildId)
            .OrderByDescending(e => e.Id)
            .Take(100)
            .ToArrayAsync(cancellationToken);

        Array.Reverse(entries);
        return entries;
    }
}
