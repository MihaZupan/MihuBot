using Discord;
using Discord.WebSocket;
using MihuBot.Configuration;
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

        private readonly Dictionary<ulong, Duel> _channelDuels = new ();
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, (int, int)>> _leaderboard = new("DuelsLeaderboard.json");

        private readonly IConfigurationService _configuration;

        public DuelCommand(IConfigurationService configuration)
        {
            _configuration = configuration;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (ctx.Arguments.Length == 1 && ctx.Arguments[0].Equals("leaderboard", StringComparison.OrdinalIgnoreCase))
            {
                var top10 =
                    (await _leaderboard.QueryAsync(l => l.ToArray()))
                    .OrderByDescending(l => ((float)l.Value.Item1 * l.Value.Item1) / (l.Value.Item1 + l.Value.Item2))
                    .ThenBy(l => l.Value.Item1)
                    .Take(10)
                    .ToArray();

                var lines = top10.Select(t => $"{ctx.Discord.GetUser(t.Key).GetName(),-16} {t.Value.Item1}/{t.Value.Item1 + t.Value.Item2}");
                await ctx.ReplyAsync($"```\n{string.Join('\n', lines)}\n```");

                return;
            }

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

                await Task.Delay(TimeSpan.FromSeconds(3));

                await ctx.ReplyAsync($"Something interesting happened {Emotes.DarlFighting}");

                await Task.Delay(TimeSpan.FromSeconds(2));

                var (winner, looser) = duel.ChooseWinner(_configuration);
                await ctx.ReplyAsync($"{winner.GetName()} won! {Emotes.WeeHypers}");

                var leaderboard = await _leaderboard.EnterAsync();
                try
                {
                    leaderboard.TryGetValue(winner.Id, out var score);
                    leaderboard[winner.Id] = (score.Item1 + 1, score.Item2);

                    leaderboard.TryGetValue(looser.Id, out score);
                    leaderboard[looser.Id] = (score.Item1, score.Item2 + 1);
                }
                finally
                {
                    _leaderboard.Exit();
                }
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

            public (SocketGuildUser winner, SocketGuildUser looser) ChooseWinner(IConfigurationService configuration)
            {
                const int OneInX = 1000;

                SocketGuildUser winner = null;

                if (configuration.TryGet(null, $"Duels.{UserOne.Id}", out string chanceOne) |
                    configuration.TryGet(null, $"Duels.{UserTwo.Id}", out string chanceTwo))
                {
                    int? oddsOne = chanceOne != null && int.TryParse(chanceOne, out int oddsOneValue) && oddsOneValue >= 0 && oddsOneValue <= OneInX
                        ? oddsOneValue : null;

                    int? oddsTwo = chanceTwo != null && int.TryParse(chanceTwo, out int oddsTwoValue) && oddsTwoValue >= 0 && oddsTwoValue <= OneInX
                        ? oddsTwoValue : null;

                    if (oddsOne != oddsTwo && (oddsOne.HasValue || oddsTwo.HasValue))
                    {
                        if (oddsOne.HasValue && oddsTwo.HasValue)
                        {
                            double ratio = oddsOne.Value / (double)(oddsOne.Value + oddsTwo.Value);

                            winner = ratio != 0 && (ratio == 1 || Rng.Next(OneInX * OneInX) <= ratio * (OneInX * OneInX))
                                ? UserOne : UserTwo;
                        }
                        else
                        {
                            int odds = oddsOne ?? oddsTwo.Value;

                            winner = odds > 0 && (odds == OneInX || Rng.Next(OneInX) < odds)
                                ? (oddsOne.HasValue ? UserOne : UserTwo)
                                : (oddsOne.HasValue ? UserTwo : UserOne);
                        }
                    }
                }

                if (winner is null)
                    winner = Rng.Bool() ? UserOne : UserTwo;

                return (winner, UserOne == winner ? UserTwo : UserOne);
            }
        }
    }
}
