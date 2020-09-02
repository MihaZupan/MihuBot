using Discord;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.Helpers
{
    public static class Helpers
    {
        public static async Task ReplyAsync(this SocketMessage message, string text, bool mention = false)
        {
            await message.Channel.SendMessageAsync(mention ? string.Concat(MentionUtils.MentionUser(message.Author.Id), " ", text) : text);
        }

        public static SocketGuild Guild(this SocketMessage message)
        {
            return (message.Channel as SocketGuildChannel).Guild;
        }

        public static SocketGuild Guild(this ISocketMessageChannel channel)
        {
            return (channel as SocketGuildChannel).Guild;
        }

        public static bool IsAdminFor(this SocketUser user, SocketGuild guild)
        {
            return Constants.Admins.Contains(user.Id) || (Constants.GuildMods.TryGetValue(guild.Id, out var guildMods) && guildMods.Contains(user.Id));
        }

        public static bool AuthorIsAdmin(this SocketMessage message)
        {
            return message.Author.IsAdminFor(message.Guild());
        }

        public static bool AuthorHasSafePermissions(this SocketMessage message)
        {
            var guild = message.Guild();

            if (guild.Id != Guilds.DDs)
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

        public static async Task<IUser> GetRandomChannelUserAsync(this ISocketMessageChannel channel)
        {
            var userLists = await channel.GetUsersAsync(CacheMode.AllowDownload).ToArrayAsync();
            var users = userLists.SelectMany(i => i).ToArray();
            return users[Rng.Next(users.Length)];
        }

        public static Task<T> CancelAfter<T>(this Task<T> task, TimeSpan timeSpan)
        {
            if (task.IsCompleted)
                return task;

            var tcs = new TaskCompletionSource<T>();

            var timer = new Timer(s =>
            {
                ((TaskCompletionSource<T>)s).TrySetException(new TimeoutException());
            }, tcs, timeSpan, Timeout.InfiniteTimeSpan);

            task.ContinueWith((t, s) =>
                {
                    var state = (Tuple<TaskCompletionSource<T>, Timer>)s;
                    state.Item2.Dispose();

                    if (t.IsCompletedSuccessfully)
                    {
                        state.Item1.TrySetResult(t.Result);
                    }
                    else
                    {
                        state.Item1.TrySetException(t.Exception);
                    }
                },
                Tuple.Create(tcs, timer),
                cancellationToken: default,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Current);

            return tcs.Task;
        }
    }
}
