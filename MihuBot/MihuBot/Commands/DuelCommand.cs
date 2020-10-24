using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class DuelCommand : CommandBase
    {
        public override string Command => "duel";

        private readonly Dictionary<ulong, Duel> _channelDuels = new Dictionary<ulong, Duel>();

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            var (target, error) = await TryGetTargetAsync(ctx);

            Duel duel;
            bool isNewDuel = false;
            lock (_channelDuels)
            {
                if (!_channelDuels.TryGetValue(ctx.Channel.Id, out duel) && target != null)
                {
                    isNewDuel = true;
                    duel = new Duel(ctx.Author, target);
                    _channelDuels.Add(ctx.Channel.Id, duel);
                }
            }

            if (duel is null)
            {
                Debug.Assert(target is null && error != null);
                await ctx.ReplyAsync(error, mention: true);
                return;
            }
            else if (!isNewDuel)
            {
                await ctx.ReplyAsync("A duel is already in progress", mention: true);
                return;
            }

            await StartDuelAsync(ctx, duel);
        }

        private async Task<(SocketGuildUser Target, string Error)> TryGetTargetAsync(CommandContext ctx)
        {
            if (ctx.Arguments.Length == 0)
                return (null, "Who're you going against?");

            SocketGuildUser target = null;

            var mentionedUsers = ctx.Message.MentionedUsers;
            if (mentionedUsers.Count != 0)
            {
                if (mentionedUsers.Count != 1)
                {
                    return (null, "You can only duel one person at a time");
                }

                IUser mentioned = mentionedUsers.Single();
                if (mentioned != null)
                {
                    target = ctx.Guild.GetUser(mentioned.Id);
                }
            }
            else if (ulong.TryParse(ctx.Arguments[0], out ulong userId))
            {
                target = ctx.Guild.GetUser(userId);
            }
            else
            {
                string pattern = ctx.Arguments[0];

                var users = await ctx.Channel.GetUsersAsync().ToArrayAsync();
                var matches = users
                    .SelectMany(i => i)
                    .Where(u =>
                        u.Username.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                        (ctx.Guild.GetUser(u.Id)?.Nickname?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToArray();

                if (matches.Length > 1)
                    matches = matches.Where(u => u.Id != ctx.AuthorId).ToArray();

                if (matches.Length > 1)
                    matches = matches.Where(u => u.Id != KnownUsers.Miha).ToArray();

                if (matches.Length > 1)
                {
                    return (null, "Please be more specific");
                }

                IUser match = matches.SingleOrDefault();
                if (match != null)
                {
                    target = ctx.Guild.GetUser(match.Id);
                }
            }

            if (target is null)
            {
                return (null, "Sorry, I don't know that person");
            }

            if (target.Id == ctx.AuthorId)
            {
                return (null, "You can't duel yourself");
            }

            return (target, null);
        }

        private async Task StartDuelAsync(MessageContext ctx, Duel duel)
        {
            try
            {
                await ctx.ReplyAsync($"{duel.UserOne.GetName()} is going up against {duel.UserTwo.GetName()} {Emotes.DarlFighting}");

                await Task.Delay(TimeSpan.FromSeconds(5));

                await ctx.ReplyAsync($"Something interesting happened {Emotes.DarlFighting}");

                await Task.Delay(TimeSpan.FromSeconds(3));

                IUser winner = Rng.Bool() ? duel.UserOne : duel.UserTwo;
                await ctx.ReplyAsync($"{winner} won! {Emotes.WeeHypers}");
            }
            finally
            {
                lock (_channelDuels)
                {
                    _channelDuels.Remove(ctx.Channel.Id);
                }
            }
        }

        private sealed class Duel
        {
            public readonly SocketGuildUser UserOne, UserTwo;

            public Duel(SocketGuildUser userOne, SocketGuildUser userTwo)
            {
                UserOne = userOne;
                UserTwo = userTwo;
            }
        }
    }
}
