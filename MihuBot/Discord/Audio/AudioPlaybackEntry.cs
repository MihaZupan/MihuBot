using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace MihuBot.Discord.Audio;

public enum AudioPlaybackOutcome
{
    Playing,
    Completed,
    Skipped,
    Failed,
    Stopped
}

[Table("audioPlaybackHistory")]
[Index(nameof(GuildId), nameof(Id))]
public sealed class AudioPlaybackEntry
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong? RequesterId { get; set; }
    public string Title { get; set; }
    public string Url { get; set; }
    public bool IsAutoplay { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public AudioPlaybackOutcome Outcome { get; set; }
}
