namespace MihuBot.Reminders
{
    public class ReminderEntry : IComparable<ReminderEntry>
    {
        public DateTime Time { get; set; }
        public string Message { get; set; }
        public ulong GuildId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong MessageId { get; set; }
        public ulong AuthorId { get; set; }

        // For serialization
        public ReminderEntry() { }

        public ReminderEntry(DateTime time, string message, MessageContext ctx)
        {
            Time = time;
            Message = message;
            GuildId = ctx.Guild.Id;
            ChannelId = ctx.Channel.Id;
            MessageId = ctx.Message.Id;
            AuthorId = ctx.AuthorId;
        }

        public int CompareTo(ReminderEntry other) => Time.CompareTo(other.Time);

        public override string ToString()
        {
            return $"{Time.ToISODateTime()}: {Message}";
        }
    }
}
