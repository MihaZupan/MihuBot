namespace MihuBot.Reminders
{
    internal sealed class ReminderService : IReminderService
    {
        private readonly PriorityQueue<ReminderEntry, DateTime> _remindersHeap = new(32);
        private readonly SynchronizedLocalJsonStore<List<ReminderEntry>> _reminders = new("Reminders.json");
        private readonly Logger _logger;

        public ReminderService(Logger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.DebugLog($"Initializing {nameof(ReminderService)}");

            List<ReminderEntry> reminders = _reminders.DangerousGetValue();

            foreach (var reminder in reminders)
                _remindersHeap.Enqueue(reminder, reminder.Time);
        }

        public async ValueTask<IEnumerable<ReminderEntry>> GetAllRemindersAsync()
        {
            return await _reminders.QueryAsync(i => i.ToArray());
        }

        public async ValueTask<IEnumerable<ReminderEntry>> GetRemindersForUserAsync(ulong userId)
        {
            return await _reminders.QueryAsync(reminders => reminders
                .Where(r => r.AuthorId == userId)
                .ToArray());
        }

        public async ValueTask<ICollection<ReminderEntry>> GetPendingRemindersAsync()
        {
            var now = DateTime.UtcNow;
            List<ReminderEntry> entries = null;

            try
            {
                lock (_remindersHeap)
                {
                    while (_remindersHeap.Count != 0 && _remindersHeap.Peek().Time < now)
                    {
                        var entry = _remindersHeap.Dequeue();
                        Log($"Popping reminder from the heap {entry}", entry);
                        (entries ??= new List<ReminderEntry>()).Add(entry);
                    }
                }

                if (entries != null)
                {
                    List<ReminderEntry> reminders = await _reminders.EnterAsync();
                    try
                    {
                        for (int i = 0; i < entries.Count; i++)
                        {
                            if (!reminders.Remove(entries[i]))
                            {
                                entries.RemoveAt(i);
                                i--;
                            }
                        }
                    }
                    finally
                    {
                        _reminders.Exit();
                    }

                    entries.RemoveAll(r => now - r.Time > TimeSpan.FromMinutes(1));
                }
            }
            catch (Exception ex)
            {
                _logger.DebugLog(ex.ToString());
            }

            return entries ?? (ICollection<ReminderEntry>)Array.Empty<ReminderEntry>();
        }

        public async ValueTask ScheduleAsync(ReminderEntry entry)
        {
            Log($"Setting reminder entry for {entry}", entry);

            List<ReminderEntry> reminders = await _reminders.EnterAsync();
            try
            {
                reminders.Add(entry);
                lock (_remindersHeap)
                {
                    _remindersHeap.Enqueue(entry, entry.Time);
                }
            }
            finally
            {
                _reminders.Exit();
            }
        }

        public async ValueTask<int> RemoveRemindersAsync(ReadOnlyMemory<ulong> toRemove)
        {
            List<ReminderEntry> reminders = await _reminders.EnterAsync();
            try
            {
                return reminders.RemoveAll(r => toRemove.Span.Contains(r.MessageId));
            }
            finally
            {
                _reminders.Exit();
            }
        }

        private void Log(string message, ReminderEntry entry) =>
            _logger.DebugLog(message, entry.GuildId, entry.ChannelId, entry.MessageId, entry.AuthorId);
    }
}
