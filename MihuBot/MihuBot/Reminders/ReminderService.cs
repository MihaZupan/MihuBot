using MihuBot.Helpers;
using SharpCollections.Generic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Reminders
{
    internal sealed class ReminderService : IReminderService
    {
        private readonly BinaryHeap<ReminderEntry> _remindersHeap =
            new BinaryHeap<ReminderEntry>(32);

        private readonly SynchronizedLocalJsonStore<List<ReminderEntry>> _reminders =
            new SynchronizedLocalJsonStore<List<ReminderEntry>>("Reminders.json");

        private readonly Logger _logger;

        public ReminderService(Logger logger)
        {
            _logger = logger;
            _logger.DebugLog($"Initializing {nameof(ReminderService)}");

            List<ReminderEntry> reminders = _reminders.DangerousGetValue();

            foreach (var reminder in reminders)
                _remindersHeap.Push(reminder);
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

        public async ValueTask<IEnumerable<ReminderEntry>> GetPendingRemindersAsync()
        {
            var now = DateTime.UtcNow;
            List<ReminderEntry> entries = null;

            try
            {
                lock (_remindersHeap)
                {
                    while (!_remindersHeap.IsEmpty && _remindersHeap.Top.Time < now)
                    {
                        var entry = _remindersHeap.Pop();
                        Log($"Popping reminder from the heap {entry}", entry);
                        (entries ??= new List<ReminderEntry>()).Add(entry);
                    }
                }

                if (entries != null)
                {
                    List<ReminderEntry> reminders = await _reminders.EnterAsync();
                    try
                    {
                        foreach (var entry in entries)
                            reminders.Remove(entry);
                    }
                    finally
                    {
                        _reminders.Exit();
                    }

                    entries.RemoveAll(r => now - r.Time > TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                _logger.DebugLog(ex.ToString());
            }

            return entries ?? (IEnumerable<ReminderEntry>)Array.Empty<ReminderEntry>();
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
                    _remindersHeap.Push(entry);
                }
            }
            finally
            {
                _reminders.Exit();
            }
        }

        private void Log(string message, ReminderEntry entry) =>
            _logger.DebugLog(message, guildId: entry.GuildId, channelId: entry.ChannelId, authorId: entry.AuthorId);
    }
}
