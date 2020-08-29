using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class AmongUsCommand : CommandBase
    {
        public override string Command => "amongus";

        protected override int CooldownToleranceCount => 2;
        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            SocketVoiceChannel voiceChannel = ctx.Guild.VoiceChannels.FirstOrDefault(vc => vc.Users.Any(u => u.Id == ctx.AuthorId));

            if (voiceChannel is null)
            {
                await ctx.ReplyAsync("Join a VC first", mention: true);
                return;
            }

            SocketGuildUser[] players = voiceChannel.Users
                .Where(u => !u.IsBot && !u.IsMuted && !u.IsSelfMuted && !u.IsDeafened && !u.IsSelfDeafened)
                .ToArray();

            if (players.Length < 2)
            {
                await ctx.ReplyAsync("Wait for a few more players to join", mention: true);
                return;
            }

            if (players.Length > 10)
            {
                await ctx.ReplyAsync("Too many people - make sure everyone who isn't playing is muted", mention: true);
                return;
            }

            const int NumOptionsWithPartners = 4;
            const int NumOptionsWithoutPartners = 14;
            const int NumOptions = NumOptionsWithPartners + NumOptionsWithoutPartners;

            if (players.Length % 2 == 0 && Rng.Next(NumOptions) < NumOptionsWithPartners)
            {
                var pairs = Rng.GeneratePairs(players);

                switch (Rng.Next(NumOptionsWithPartners) + 1)
                {
                    case 1:
                        await SendEmbedAsync(ctx, "GRIEF", $"If your partner dies, you drink!\n```{GetNamePairs(pairs, " - ")}```");
                        break;

                    case 2:
                        await SendEmbedAsync(ctx, "NAME SWAP", $"Swap your IGNs\n```{GetNamePairs(pairs, " - ")}```");
                        break;

                    case 3:
                        await SendEmbedAsync(ctx, "DRINKING BUDDY",
                            "Your partner drinks, you drink!\n" + $"```{GetNamePairs(pairs, " - ")}```");
                        break;

                    case 4:
                        await SendEmbedAsync(ctx, "RENAMED",
                            "Everyone gets renamed. Called a person by their real name? Drink!\n" +
                            $"```{string.Join('\n', pairs.Select(p => $"{GetName(p.First)} chooses an IGN for {GetName(p.Second)}"))}```");
                        break;

                    default:
                        throw new InvalidOperationException();
                }
            }
            else
            {
                switch (Rng.Next(NumOptionsWithoutPartners) + 1)
                {
                    case 1:
                        await SendEmbedAsync(ctx, "LIVER SCAN", "Getting scanned in the MedBay? Drink!");
                        break;

                    case 2:
                        await SendEmbedAsync(ctx, "REST FOR THE DEAD", "Ghosts can't do tasks until after a meeting following their death");
                        break;

                    case 3:
                        await SendEmbedAsync(ctx, "WATERFALL", "First to die next game starts it off");
                        break;

                    case 4:
                        await SendEmbedAsync(ctx, "COVERT DRINK", "If an imposter dies, the other imposter drinks");
                        break;

                    case 5:
                        string name = players.Any(u => u.Id == KnownUsers.James)
                            ? "James"
                            : name = GetName(players.Random());

                        await SendEmbedAsync(ctx,
                            $"{name.ToUpperInvariant()}{((name[^1] | 0x20) == 's' ? "'" : "'s")} CURSE",
                            $"If {name} is voted off in the round, survivors drink");
                        break;

                    case 6:
                        await SendEmbedAsync(ctx, "NOTHING TO DISCUSS",
                            "The first meeting has a mandatory waterfall for the living.\n" +
                            "Whoever called the meeting/discovered the body starts.\n" +
                            "No talking until the last person is finished.");
                        break;

                    case 7:
                        await SendEmbedAsync(ctx, "VAMPIRE", "Imposters drink for each personal kill in the round");
                        break;

                    case 8:
                        await SendEmbedAsync(ctx, "GORLS DRINK", "All females drink");
                        break;

                    case 9:
                        await SendEmbedAsync(ctx, "BOIZ DRINK", "All males drink");
                        break;

                    case 10:
                        await SendEmbedAsync(ctx, "PULL YOUR WEIGHT", "Died as an imposter with no kills? Drink!");
                        break;

                    case 11:
                        await SendEmbedAsync(ctx, "ROLEPLAY",
                            "**If you speak out of character, you drink:** " +
                            new[]
                            {
                                "Russians",
                                "Cali/Surfer/Chads",
                                "Swollen tongue/lisp",
                                "ASMR/whispers",
                                "Christian Bale Batman",
                                "Space crew/walkie talkie"
                            }.Random());
                        break;

                    case 12:
                        await SendEmbedAsync(ctx, "RULES",
                            "**Break the rule, you drink:** " +
                            new[]
                            {
                                "No swearing during conferences",
                                "Can't say \"body\"",
                                "You can't say where the body is",
                                "Discoverer can't talk until voting period",
                                "Speak in the majestic plural \"We were in Medbay\"",
                                "\"...in bed\" at the end of every sentence"
                            }.Random());
                        break;

                    case 13:
                        {
                            int shift = Rng.Next(players.Length - 1) + 1;
                            var combinations = players.Select((p, i) => (p, players[(i + shift) % players.Length])).ToArray();
                            await SendEmbedAsync(ctx, "NAME SHIFT", $"Change your IGNs\n```{GetNamePairs(combinations, ": ")}```");
                        }
                        break;

                    case 14:
                        {
                            var combinations = players.Select(p => (p, players.RandomExcludingSelf(p))).ToArray();
                            await SendEmbedAsync(ctx, "GRIEF", $"If your assigned person dies, you drink!\n```{GetNamePairs(combinations, ": ")}```");
                        }
                        break;

                    default:
                        throw new InvalidOperationException();
                }
            }

            static async Task SendEmbedAsync(CommandContext ctx, string title, string description)
            {
                Embed embed = new EmbedBuilder()
                    .WithTitle($"**{title}**")
                    .WithDescription(description)
                    .WithColor(Rng.Next(256), Rng.Next(256), Rng.Next(256))
                    .Build();

                await ctx.Channel.SendMessageAsync(
                    text: "**Drunk Among Us**\n" +
                        "You die: you drink\n" +
                        "Team loses: team drinks\n" +
                        "Emergency meeting: everyone drinks\n" +
                        "Body discovered: all but discoverer drinks",
                    embed: embed);
            }

            static string GetName(SocketGuildUser user)
            {
                string name = user.Nickname ?? user.Username;

                if (name.Contains('|'))
                    name = name.Substring(name.IndexOf('|') + 1).Trim();

                return name;
            }

            static string GetNamePairs((SocketGuildUser First, SocketGuildUser Second)[] pairs, string separator)
            {
                return string.Join('\n', pairs.Select(p => $"{GetName(p.First)}{separator}{GetName(p.Second)}"));
            }
        }
    }
}
