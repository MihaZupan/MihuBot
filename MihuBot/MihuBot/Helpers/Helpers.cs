using AspNet.Security.OAuth.Discord;
using Discord.Rest;
using System.Buffers;
using System.Security.Claims;

namespace MihuBot.Helpers;

public static class Helpers
{
    public static string GetName(this SocketGuildUser user)
    {
        string name = user.Nickname ?? user.Username;

        int splitIndex = name.IndexOf('|');

        if (splitIndex >= 0)
            name = name.AsSpan(splitIndex + 1).Trim().ToString();

        return name;
    }

    public static string GetName(this IUser user)
    {
        return (user as SocketGuildUser)?.GetName() ?? user.Username;
    }

    public static string GetAvatarUrl(this ClaimsPrincipal claims, ushort size)
    {
        string avatarHash = claims.FindFirstValue(DiscordAuthenticationConstants.Claims.AvatarHash);
        return CDN.GetUserAvatarUrl(claims.GetDiscordUserId(), avatarHash, size, ImageFormat.WebP);
    }

    public static IEnumerable<T> Unique<T>(this IEnumerable<T> source)
    {
        var hashSet = new HashSet<T>();

        foreach (T element in source)
        {
            if (hashSet.Add(element))
            {
                yield return element;
            }
        }
    }

    public static bool OverwritesEqual(this IReadOnlyCollection<Overwrite> left, IReadOnlyCollection<Overwrite> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        using IEnumerator<Overwrite> e1 = left.GetEnumerator();
        using IEnumerator<Overwrite> e2 = right.GetEnumerator();

        while (e1.MoveNext())
        {
            if (!e2.MoveNext())
            {
                return false;
            }

            if (!e1.Current.OverwriteEquals(e2.Current))
            {
                return false;
            }
        }

        return !e2.MoveNext();
    }

    public static bool OverwriteEquals(this Overwrite left, Overwrite right)
    {
        return left.TargetId == right.TargetId &&
            left.TargetType == right.TargetType &&
            left.Permissions.AllowValue == right.Permissions.AllowValue &&
            left.Permissions.DenyValue == right.Permissions.DenyValue;
    }

    public static string GetJumpUrl(this SocketGuildChannel channel)
    {
        return GetJumpUrl(channel.Guild.Id, channel.Id);
    }

    public static string GetJumpUrl(ulong guildId, ulong channelId)
    {
        return $"https://discord.com/channels/{guildId}/{channelId}";
    }

    public static string GetJumpUrl(ulong guildId, ulong channelId, ulong messageId)
    {
        return $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";
    }

    public static async Task<RestUserMessage> SendTextFileAsync(this SocketTextChannel channel, string name, string content)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(content.Length));
        try
        {
            int length = Encoding.UTF8.GetBytes(content, bytes);
            var ms = new MemoryStream(bytes, 0, length);
            return await channel.SendFileAsync(ms, name);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static Task<RestUserMessage> SendFileAsync(this SocketTextChannel channel, Stream stream, string filename, string text = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, bool isSpoiler = false)
    {
        return channel.SendFileAsync(stream, filename, text, isTTS, embed, options, isSpoiler);
    }

    public static IAsyncEnumerable<IReadOnlyCollection<IUser>> GetUsersAsync(this SocketTextChannel channel, CacheMode mode = CacheMode.AllowDownload, RequestOptions options = null)
    {
        // TODO: Why is this needed?
        return ((IChannel)channel).GetUsersAsync(mode, options);
    }

    public static SocketTextChannel GetTextChannel(this DiscordSocketClient client, ulong channelId)
    {
        return client.GetChannel(channelId) as SocketTextChannel;
    }

    public static SocketGuild Guild(this SocketMessage message)
    {
        return (message.Channel as SocketGuildChannel)?.Guild;
    }

    public static SocketGuild Guild(this ISocketMessageChannel channel)
    {
        return (channel as SocketGuildChannel)?.Guild;
    }

    public static bool TryGetFirst<T>(this IEnumerable<T> entities, ulong id, out T entity)
        where T : ISnowflakeEntity
    {
        foreach (var e in entities)
        {
            if (e.Id == id)
            {
                entity = e;
                return true;
            }
        }

        entity = default;
        return false;
    }

    public static bool Any<T>(this IEnumerable<T> entities, ulong id)
        where T : ISnowflakeEntity
    {
        foreach (var e in entities)
        {
            if (e.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    public static bool SequenceIdEquals<T>(this IEnumerable<T> first, IEnumerable<T> second)
        where T : ISnowflakeEntity
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is ICollection<T> collection1 && second is ICollection<T> collection2)
        {
            if (collection1.Count != collection2.Count)
            {
                return false;
            }
        }

        using IEnumerator<T> e1 = first.GetEnumerator();
        using IEnumerator<T> e2 = second.GetEnumerator();

        while (e1.MoveNext())
        {
            if (!e2.MoveNext())
            {
                return false;
            }

            if (e1.Current.Id != e2.Current.Id)
            {
                return false;
            }
        }

        return !e2.MoveNext();
    }

    public static bool All<T>(this Predicate<T>[] predicates, T value)
    {
        foreach (var predicate in predicates)
        {
            if (!predicate(value))
            {
                return false;
            }
        }
        return true;
    }

    public static async Task<IUser> GetRandomChannelUserAsync(this ISocketMessageChannel channel, params ulong[] exclusions)
    {
        var userLists = await channel
            .GetUsersAsync(CacheMode.AllowDownload)
            .ToArrayAsync();

        var users = userLists
            .SelectMany(i => i)
            .Where(i => !exclusions.Contains(i.Id))
            .ToArray();

        return users.Random();
    }

    public static async Task<IMessage[]> DangerousGetAllMessagesAsync(this ITextChannel channel, Logger logger, string auditReason)
    {
        logger.DebugLog($"Fetching all messages for {channel.Name}", guildId: channel.GuildId, channelId: channel.Id);

        var options = string.IsNullOrWhiteSpace(auditReason) ? null : new RequestOptions()
        {
            AuditLogReason = auditReason
        };

        var messagesSource = channel.GetMessagesAsync(limit: int.MaxValue, options: options);

        return (await messagesSource.ToArrayAsync())
            .SelectMany(i => i)
            .ToArray();
    }

    public static string ToISODate(this DateTime date) => date.ToString("yyyy-MM-dd");

    public static string ToISODate(this DateTimeOffset date) => ToISODate(date.UtcDateTime);

    public static string ToISODateTime(this DateTime dateTime, char separator = '_') => dateTime.ToString($"yyyy-MM-dd{separator}HH-mm-ss");

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

    public static async Task<TResult> TimeoutAfter<TResult>(this Task<TResult> task, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource();

        if (task == await Task.WhenAny(task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false))
        {
            cts.Cancel();
            return await task.ConfigureAwait(false);
        }
        else
        {
            throw new TimeoutException($"Task timed out after {timeout}");
        }
    }

    public static async Task<bool> TryDeleteAsync(this RestUserMessage message)
    {
        if (message.Channel is not SocketTextChannel textChannel ||
            textChannel.GetUser(message.Author.Id)?.GetPermissions(textChannel).ManageMessages != false)
        {
            try
            {
                await message.DeleteAsync();
                return true;
            }
            catch { }
        }

        return false;
    }

    public static async Task<IUserMessage> TrySendMessageAsync(this ITextChannel channel, string text = null, bool isTTS = false, Embed embed = null, RequestOptions options = null, AllowedMentions allowedMentions = null, MessageReference messageReference = null, MessageComponent components = null, ISticker[] stickers = null, Embed[] embeds = null, Logger logger = null)
    {
        if (channel is null)
        {
            logger?.DebugLog($"Failed to send a message because the provided channel is null: Text='{text}', Embed='{embed}'");
            return null;
        }

        try
        {
            return await channel.SendMessageAsync(text, isTTS, embed, options, allowedMentions, messageReference, components, stickers, embeds);
        }
        catch (Exception ex)
        {
            logger?.DebugLog($"Failed to send a message because an exception was thrown: Text='{text}', Embed='{embed}', Exception: {ex}", channel.GuildId, channel.Id);
            return null;
        }
    }

    public static async Task<IUserMessage> TrySendFilesAsync(this ITextChannel channel, IEnumerable<FileAttachment> attachments, string text = null, Logger logger = null)
    {
        if (channel is null)
        {
            logger?.DebugLog($"Failed to send the files because the provided channel is null: Text='{text}', AttachmentCount='{attachments.Count()}'");
            return null;
        }

        try
        {
            return await channel.SendFilesAsync(attachments, text);
        }
        catch (Exception ex)
        {
            logger?.DebugLog($"Failed to send the files because an exception was thrown: Text='{text}', AttachmentCount='{attachments.Count()}', Exception: {ex}", channel.GuildId, channel.Id);
            return null;
        }
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
}
