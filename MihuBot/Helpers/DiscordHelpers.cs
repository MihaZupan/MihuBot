using AspNet.Security.OAuth.Discord;
using Discord.Rest;
using System.Buffers;
using System.Security.Claims;

namespace MihuBot.Helpers;

public static class DiscordHelpers
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

    public static async Task<RestUserMessage> SendTextFileAsync(this SocketTextChannel channel, string name, string content, string messageText = null, MessageComponent components = null)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(content.Length));
        try
        {
            int length = Encoding.UTF8.GetBytes(content, bytes);
            var ms = new MemoryStream(bytes, 0, length);
            return await channel.SendFileAsync(ms, name, messageText, components: components);
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
}
