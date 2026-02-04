using System.Reflection;

#nullable enable

namespace MihuBot.Helpers;

public static class SharedHelpers
{
    public static void Toggle(ref this bool value)
    {
        value = !value;
    }

    public static void AddRange<T>(this HashSet<T> set, IEnumerable<T> items)
    {
        foreach (T? item in items)
        {
            set.Add(item);
        }
    }

    public static string GetRoughSizeString(long size)
    {
        double kb = size / 1024d;
        double mb = kb / 1024d;
        double gb = mb / 1024d;

        if (gb >= 1)
        {
            int gbFraction = (int)(gb % 1 * 10);
            return $"{(int)gb}.{gbFraction} GB";
        }

        if (mb >= 1)
        {
            return $"{(int)mb} MB";
        }

        if (kb >= 1)
        {
            return $"{(int)kb} KB";
        }

        return $"{size} B";
    }

    public static string ToISODate(this DateTime date) => date.ToString("yyyy-MM-dd");

    public static string ToISODate(this DateTimeOffset date) => ToISODate(date.UtcDateTime);

    public static string ToISODateTime(this DateTime dateTime, char separator = '_') => dateTime.ToString($"yyyy-MM-dd{separator}HH-mm-ss");

    public static string ToISODateTime(this DateTimeOffset date, char separator = '_') => ToISODateTime(date.UtcDateTime, separator);

    public static string ToElapsedTime(this TimeSpan elapsed, bool includeSeconds = true)
    {
        if (elapsed.TotalMinutes < 1)
        {
            return includeSeconds && elapsed.TotalSeconds > 0
                ? GetSeconds(elapsed.Seconds)
                : "0 minutes";
        }

        if (elapsed.TotalHours < 1)
        {
            return elapsed.Seconds == 0 || !includeSeconds
                ? GetMinutes(elapsed.Minutes)
                : $"{GetMinutes(elapsed.Minutes)} {GetSeconds(elapsed.Seconds)}";
        }

        if (elapsed.TotalDays < 1)
        {
            return elapsed.Minutes == 0 && elapsed.Seconds == 0
                ? GetHours((int)elapsed.TotalHours)
                : $"{GetHours((int)elapsed.TotalHours)} {GetMinutes(elapsed.Minutes)}";
        }

        if (elapsed.TotalDays < 365)
        {
            return elapsed.Hours == 0 && elapsed.Minutes == 0 && elapsed.Seconds == 0
                ? GetDays((int)elapsed.TotalDays)
                : $"{GetDays((int)elapsed.TotalDays)} {GetHours(elapsed.Hours)}";
        }

        int years = 0;
        while (elapsed.TotalDays >= 365)
        {
            int yearsToCount = Math.Max(1, (int)(elapsed.TotalDays / 366));
            years += yearsToCount;

            DateTime now = DateTime.UtcNow;
            DateTime future = now.AddYears(yearsToCount);
            elapsed -= future - now;
        }

        if (elapsed.TotalDays >= 1)
        {
            return $"{GetYears(years)} {GetDays((int)elapsed.TotalDays)}";
        }

        return GetYears(years);

        static string GetSeconds(int number) => GetString(number, "second");
        static string GetMinutes(int number) => GetString(number, "minute");
        static string GetHours(int number) => GetString(number, "hour");
        static string GetDays(int number) => GetString(number, "day");
        static string GetYears(int number) => GetString(number, "year");

        static string GetString(int number, string type) => $"{number} {type}{(number == 1 ? "" : "s")}";
    }

    public static T[] InitializeWithDefaultCtor<T>(this T[] array)
        where T : new()
    {
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = new T();
        }

        return array;
    }

    public static void IgnoreExceptions(this Task task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            task.ContinueWith(
                static task => _ = task.Exception?.InnerException,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Current);
        }
    }

    public static string GetCommitId()
    {
        return GetCommitId(typeof(Program).Assembly);
    }

    public static string GetCommitId(Assembly assembly)
    {
        string? commit = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (commit is not null)
        {
            int plusOffset = commit.IndexOf('+');
            if (plusOffset >= 0)
            {
                commit = commit.Substring(plusOffset + 1);
            }
        }

        return commit ?? "unknown";
    }
}
