using MihuBot.DB;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace MihuBot.Agents;

internal static class AgentDiscordTools
{
    internal sealed record MessageSearch(long? AuthorId = null, string Text = null, long After = 0, bool? HasAttachments = null, bool IncludeDeleted = true);
    internal sealed record DiscordUser(string Id, string Username, string DisplayName, string Nickname, bool IsBot);

    internal static long GetHistoryCursor(string beforeMessageId, ulong requestId)
    {
        if (beforeMessageId is null)
        {
            return checked((long)requestId);
        }

        return Math.Min(ParseId(beforeMessageId, nameof(beforeMessageId)), checked((long)requestId));
    }

    internal static long ParseId(string value, string parameterName)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
        {
            throw new ArgumentException("Expected a positive Discord ID.", parameterName);
        }

        return id;
    }

    internal static (long After, long Before) GetSearchBounds(string after, string before, string beforeMessageId, ulong requestId)
    {
        long lower = after is null ? 0 : ParseDate(after, nameof(after));
        long upper = GetHistoryCursor(beforeMessageId, requestId);

        if (before is not null)
        {
            upper = Math.Min(upper, ParseDate(before, nameof(before)));
        }

        if (lower >= upper)
        {
            throw new ArgumentException("The search start must be before its end and the request message.", nameof(after));
        }

        return (lower, upper);

        static long ParseDate(string value, string parameterName)
        {
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ||
                date < SnowflakeUtils.FromSnowflake(0) || date > SnowflakeUtils.FromSnowflake(long.MaxValue))
            {
                throw new ArgumentException("Expected a date within Discord's supported date range, such as 2026-09-01T00:00:00Z.", parameterName);
            }

            return checked((long)SnowflakeUtils.ToSnowflake(date));
        }
    }

    internal static IQueryable<LogDbEntry> QueryHistory(IQueryable<LogDbEntry> logs, long guildId, long channelId, long before, int limit, MessageSearch search = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        search ??= new();

        var channelLogs = logs.Where(log => log.GuildId == guildId && log.ChannelId == channelId && log.Snowflake >= search.After && log.Snowflake < before &&
            (log.Type == Logger.EventType.MessageReceived || log.Type == Logger.EventType.MessageUpdated ||
             log.Type == Logger.EventType.MessageDeleted || log.Type == Logger.EventType.FileReceived));
        var candidates = channelLogs.Where(log => log.Type != Logger.EventType.MessageDeleted);

        if (search.AuthorId is { } authorId)
        {
            candidates = candidates.Where(log => log.UserId == authorId);
        }

        IQueryable<long> messageIds = candidates.Select(log => log.Snowflake).Distinct();

        if (search.Text is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(search.Text);
            string pattern = "%" + search.Text.Replace(@"\", @"\\", StringComparison.Ordinal)
                .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";

            messageIds = messageIds.Where(id => channelLogs.Any(log => log.Snowflake == id &&
                (log.Type == Logger.EventType.MessageReceived || log.Type == Logger.EventType.MessageUpdated) &&
                EF.Functions.Like(log.Content, pattern, @"\") &&
                !channelLogs.Any(newer => newer.Snowflake == id && newer.Id > log.Id &&
                    (newer.Type == Logger.EventType.MessageReceived || newer.Type == Logger.EventType.MessageUpdated))));
        }

        if (search.HasAttachments is { } hasAttachments)
        {
            messageIds = messageIds.Where(id => channelLogs.Any(log => log.Snowflake == id && log.Type == Logger.EventType.FileReceived) == hasAttachments);
        }

        if (!search.IncludeDeleted)
        {
            messageIds = messageIds.Where(id => !channelLogs.Any(log => log.Snowflake == id && log.Type == Logger.EventType.MessageDeleted));
        }

        messageIds = messageIds.OrderDescending().Take(limit);

        return channelLogs.Where(log => messageIds.Contains(log.Snowflake)).OrderBy(log => log.Id);
    }

    internal static DiscordUser[] FindUsers(IEnumerable<DiscordUser> users, string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        query = query.Trim();

        if (query.StartsWith("<@", StringComparison.Ordinal) && query.EndsWith('>'))
        {
            query = ParseId(query[2..^1].TrimStart('!'), nameof(query)).ToString(CultureInfo.InvariantCulture);
        }

        return users.Where(user => user.Id == query || Names(user).Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(user => user.Id == query || Names(user).Any(name => name.Equals(query, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.Id, StringComparer.Ordinal)
            .Take(limit).ToArray();

        static IEnumerable<string> Names(DiscordUser user) => new[] { user.Username, user.DisplayName, user.Nickname }.Where(name => name is not null);
    }

    internal static string BuildHistory(IEnumerable<LogDbEntry> entries, Func<ulong, string> getDisplayName) =>
        JsonSerializer.Serialize(entries.GroupBy(log => log.Snowflake).OrderBy(group => group.Key).Select(group =>
        {
            LogDbEntry[] ordered = [.. group.OrderBy(log => log.Id)];
            long authorId = ordered.Last(log => log.Type != Logger.EventType.MessageDeleted).UserId;
            var content = ordered.LastOrDefault(log => log.Type is Logger.EventType.MessageReceived or Logger.EventType.MessageUpdated);
            var attachments = ordered.Where(log => log.Type == Logger.EventType.FileReceived && log.ExtraContentJson is not null)
                .Select(log => JsonSerializer.Deserialize<Logger.AttachmentModel>(log.ExtraContentJson)
                    ?? throw new InvalidDataException("The logged attachment is null."))
                .DistinctBy(attachment => attachment.Id)
                .Select(attachment => new { attachment.Filename, attachment.Url });

            return new
            {
                Id = group.Key.ToString(CultureInfo.InvariantCulture),
                Author = getDisplayName((ulong)authorId),
                AuthorId = authorId.ToString(CultureInfo.InvariantCulture),
                Timestamp = SnowflakeUtils.FromSnowflake((ulong)group.Key),
                content?.Content,
                Edited = content?.Type == Logger.EventType.MessageUpdated,
                Deleted = ordered.Any(log => log.Type == Logger.EventType.MessageDeleted),
                Attachments = attachments
            };
        }));

    internal static FileStream OpenUpload(string workingDirectory, string path, ulong maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        string fullPath = Path.GetFullPath(path, root);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("Uploads must be files inside the assigned working directory.", nameof(path));
        }

        for (string current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Uploads must not use symbolic links or junctions.");
            }
        }

        var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if ((ulong)file.Length > maxBytes)
        {
            file.Dispose();
            throw new ArgumentException($"The file exceeds this server's upload limit of {maxBytes} bytes.", nameof(path));
        }

        return file;
    }
}
