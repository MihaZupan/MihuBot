using System.Collections.Generic;
using System.Threading.Tasks;

namespace MihuBot.Reminders
{
    public interface IReminderService
    {
        ValueTask<IEnumerable<ReminderEntry>> GetAllRemindersAsync();

        ValueTask<IEnumerable<ReminderEntry>> GetRemindersForUserAsync(ulong userId);

        ValueTask<IEnumerable<ReminderEntry>> GetPendingRemindersAsync();

        ValueTask ScheduleAsync(ReminderEntry entry);
    }
}
