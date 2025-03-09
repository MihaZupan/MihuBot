using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace MihuBot.Reminders;

[Table("reminders")]
[Index(nameof(Time))]
public class ReminderEntry : IComparable<ReminderEntry>
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public string Message { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public long AuthorId { get; set; }
    public bool RepeatYearly { get; set; }

    // For serialization
    public ReminderEntry() { }

    public ReminderEntry(DateTime time, string message, MessageContext ctx)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ctx.AuthorId, (ulong)long.MaxValue);

        Time = time;
        Message = message;
        GuildId = ctx.Guild.Id;
        ChannelId = ctx.Channel.Id;
        MessageId = ctx.Message.Id;
        AuthorId = (long)ctx.AuthorId;
        RepeatYearly = message.Contains(" every year", StringComparison.OrdinalIgnoreCase);
    }

    public ReminderEntry GetNextYearEntry() => new()
    {
        Id = Id,
        Time = Time.AddYears(1),
        Message = Message,
        GuildId = GuildId,
        ChannelId = ChannelId,
        MessageId = MessageId,
        AuthorId = AuthorId,
        RepeatYearly = RepeatYearly,
    };

    public int CompareTo(ReminderEntry other) => Time.CompareTo(other.Time);

    public override string ToString()
    {
        return $"{Time.ToISODateTime()}: {Message}";
    }
}
