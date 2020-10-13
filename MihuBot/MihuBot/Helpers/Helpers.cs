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

        public static async Task<RestUserMessage> SendTextFileAsync(this ISocketMessageChannel channel, string name, string content)
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

        public static bool IsAdminFor(this SocketUser user, ulong guild)
        {
            return Constants.Admins.Contains(user.Id) || (Constants.GuildMods.TryGetValue(guild, out var guildMods) && guildMods.Contains(user.Id));
        }

        public static bool AuthorIsAdmin(this SocketMessage message)
        {
            var guild = message.Guild();
            return guild != null && message.Author.IsAdminFor(guild.Id);
        }

        public static bool AuthorHasSafePermissions(this SocketMessage message)
        {
            var guild = message.Guild();

            if (guild is null || guild.Id != Guilds.DDs)
                return false;

            var user = message.Author;

            return user.Id == KnownUsers.Conor
                || user.Id == KnownUsers.Sticky
                || user.Id == KnownUsers.Sfae
                || user.Id == KnownUsers.CurtIs
                || user.Id == KnownUsers.Christian
                || user.Id == KnownUsers.Maric;
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

        public static async Task<IMessage[]> DangerousGetAllMessagesAsync(this ITextChannel channel, string auditReason)
        {
            Logger.DebugLog($"Fetching all messages for {channel.Name} ({channel.GuildId}-{channel.Id})");

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
