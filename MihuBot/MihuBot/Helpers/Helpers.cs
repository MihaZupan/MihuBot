using Discord;
using Discord.Rest;
using Discord.WebSocket;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.Helpers
{
    public static class Helpers
    {
        public static string GetName(this SocketGuildUser user)
        {
            string name = user.Nickname ?? user.Username;

            int splitIndex = name.IndexOf('|');

            if (splitIndex != -1)
                name = name.Substring(splitIndex + 1).Trim();

            return name;
        }

        public static string GetName(this IUser user)
        {
            return (user as SocketGuildUser)?.GetName() ?? user.Username;
        }

        public static bool IsAdmin(this ClaimsPrincipal claims)
        {
            return claims.TryGetUserId(out ulong userId)
                && Constants.Admins.Contains(userId);
        }

        public static bool CanSendMessagesToChannel(this SocketTextChannel channel, ulong userId)
        {
            SocketGuildUser guildUser = channel.Guild.GetUser(userId);
            return guildUser != null
                && guildUser.GetPermissions(channel).SendMessages;
        }

        public static ulong GetUserId(this ClaimsPrincipal claims)
        {
            if (claims.TryGetUserId(out ulong userId))
                return userId;

            throw new Exception("Failed to get user id");
        }

        public static bool TryGetUserId(this ClaimsPrincipal claims, out ulong userId)
        {
            string id = claims.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (id != null && ulong.TryParse(id, out userId))
                return true;

            userId = 0;
            return false;
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

        public static IEnumerable<TElement> UniqueBy<TElement, TBy>(this IEnumerable<TElement> source, Func<TElement, TBy> selector)
        {
            var hashSet = new HashSet<TBy>();

            foreach (TElement element in source)
            {
                if (hashSet.Add(selector(element)))
                {
                    yield return element;
                }
            }
        }

        public static async Task ReplyAsync(this SocketMessage message, string text, bool mention = false)
        {
            await message.Channel.SendMessageAsync(mention ? string.Concat(MentionUtils.MentionUser(message.Author.Id), " ", text) : text);
        }

        public static async Task<RestUserMessage> SendTextFileAsync(this SocketTextChannel channel, string name, string content)
        {
            byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(content));
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

        public static SocketTextChannel GetTextChannel(this DiscordSocketClient client, ulong guildId, ulong channelId)
        {
            return client.GetGuild(guildId)?.GetTextChannel(channelId);
        }

        public static SocketGuild Guild(this SocketMessage message)
        {
            return (message.Channel as SocketGuildChannel)?.Guild;
        }

        public static SocketGuild Guild(this ISocketMessageChannel channel)
        {
            return (channel as SocketGuildChannel)?.Guild;
        }

        public static bool MentionsAny(this SocketUserMessage message)
        {
            return message.MentionedUsers.Any()
                || message.MentionedRoles.Any()
                || message.MentionedChannels.Any()
                || message.MentionedEveryone;
        }

        public static bool IsAdmin(this SocketUser user)
        {
            return Constants.Admins.Contains(user.Id);
        }

        public static bool AuthorIsAdmin(this SocketMessage message)
        {
            var guild = message.Guild();
            return guild != null && message.Author.IsAdmin();
        }

        public static bool TryGetFirst<T>(this IEnumerable<T> entities, ulong id, out T entity)
            where T : class, ISnowflakeEntity
        {
            foreach (var e in entities)
            {
                if (e.Id == id)
                {
                    entity = e;
                    return true;
                }
            }

            entity = null;
            return false;
        }

        public static bool Any<T>(this IEnumerable<T> entities, ulong id)
            where T : class, ISnowflakeEntity
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

        public static bool IsDreamlingsSubscriber(this SocketUser user, DiscordSocketClient client)
        {
            var guildUser = client.GetGuild(Guilds.DDs).GetUser(user.Id);

            if (guildUser != null && guildUser.Roles.Any(r => r.Id == 705711705342345246ul)) // Darlings
                return true;

            guildUser = client.GetGuild(Guilds.LiverGang).GetUser(user.Id);

            if (guildUser != null && guildUser.Roles.Any(r => r.Id == 492006735582593026ul)) // Livers
                return true;

            guildUser = client.GetGuild(Guilds.DresDreamers).GetUser(user.Id);

            if (guildUser != null && guildUser.Roles.Any(r => r.Id == 516892706467741707ul)) // Dreamers
                return true;

            //guildUser = client.GetGuild(Guilds.DDs).GetUser(user.Id);

            //if (guildUser != null && guildUser.Roles.Any(r => r.Id == 705711705342345246ul))
            //    return true;

            return false;
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

            var messagesSource = channel.GetMessagesAsync(limit: int.MaxValue, options: new RequestOptions()
            {
                AuditLogReason = auditReason
            });

            return (await messagesSource.ToArrayAsync())
                .SelectMany(i => i)
                .ToArray();
        }

        public static string ToISODate(this DateTime date) => date.ToString("yyyy-MM-dd");

        public static string ToISODate(this DateTimeOffset date) => ToISODate(date.DateTime);

        public static string ToISODateTime(this DateTime dateTime, char separator = '_') => dateTime.ToString($"yyyy-MM-dd{separator}HH-mm-ss");

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
    }
}
