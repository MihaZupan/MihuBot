using Discord;
using MihuBot.Helpers;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class FriendlyActionsCommands : CommandBase
    {
        public override string Command => "hug";
        public override string[] Aliases => new[]
        {
            "butt", "poke", "slap", "kick", "love", "kiss", "boop",
            "spank", "dropkickofftheturnbuckle", "tableflip", "spit",
            "curbstomp", "smell", "laugh", "hiton", "uwu", "beatup",
        };

        private readonly ConcurrentDictionary<ulong, ulong> _riggedRng = new ConcurrentDictionary<ulong, ulong>();

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (ctx.Guild.Id == Guilds.LiverGang && ctx.Command != "hug")
            {
                return;
            }

            if (ctx.Command == "spit" || ctx.Command == "curbstomp" || ctx.Command == "beatup")
            {
                if (!ctx.IsFromAdmin &&
                    ctx.AuthorId != KnownUsers.Sticky &&
                    ctx.AuthorId != KnownUsers.Ai &&
                    ctx.AuthorId != KnownUsers.Joster &&
                    ctx.AuthorId != KnownUsers.Awescar)
                {
                    return;
                }
            }

            if (ctx.Command == "smell")
            {
                await ctx.ReplyAsync($"{Emotes.WeirdChamp}");
                return;
            }

            bool at = false, rig = false;

            if (ctx.IsFromAdmin && ctx.Arguments.Length > 0)
            {
                at = ctx.Arguments[^1].Equals("at", StringComparison.OrdinalIgnoreCase);
                rig = ctx.Arguments[^1].Equals("rig", StringComparison.OrdinalIgnoreCase);
            }

            IUser rngUser = null;

            if (ctx.Arguments.Length > 0)
            {
                if (ctx.Message.MentionedUsers.Count != 0 && ctx.Arguments[0].StartsWith("<@") && ctx.Arguments[0].EndsWith('>'))
                {
                    rngUser = ctx.Message.MentionedUsers.Single();
                    at |= ctx.IsFromAdmin;
                }
                else if (ulong.TryParse(ctx.Arguments[0], out ulong userId))
                {
                    rngUser = ctx.Discord.GetUser(userId);
                }
            }

            if (rngUser is null)
            {
                if (_riggedRng.TryRemove(ctx.AuthorId, out ulong userId) && userId != KnownUsers.Miha)
                {
                    rngUser = ctx.Discord.GetUser(userId);
                }
                else
                {
                    rngUser = await ctx.Channel.GetRandomChannelUserAsync(KnownUsers.Miha);
                }
            }
            else if (rig)
            {
                _riggedRng.TryAdd(ctx.AuthorId, rngUser.Id);
                return;
            }

            string target = at ? MentionUtils.MentionUser(rngUser.Id) : rngUser.Username;

            bool targetIsAuthor = rngUser.Id == ctx.AuthorId;

            string reply = ctx.Command switch
            {
                "dropkickofftheturnbuckle" => $"drop kicked {target} off the turn buckle",
                "butt" => $"thinks {(targetIsAuthor ? "they have" : $"{target} has")} a nice butt! {Emotes.DarlBASS}",
                "slap" => $"just {(targetIsAuthor ? "performed a self-slap maneuver" : $"slapped {target}")}! {Emotes.MonkaHmm}",
                "kick" => $"just {(targetIsAuthor ? "tripped" : $"kicked {target}")}! {Emotes.DarlZoom}",
                "love" => $"wants {target} to know they are loved! {Emotes.DarlHearts}",
                "hug" => $"is {(targetIsAuthor ? "getting hugged" : $"sending hugs to {target}")}! {Emotes.SenpaiLove}", // TODO {Emotes.DarlHug}
                "kiss" => $"just kissed {target}! {Emotes.DarlKiss}",
                "spank" => $"just spanked {target} {Emotes.EyesShaking}",
                "tableflip" => $"just flipped {target} over the table?",
                "spit" => $"spat on {target} and called them a wh ||friend||",
                "curbstomp" => $"curb stomped {target} {Emotes.EyesShaking}",
                "poke" => $"pokes {target} {Emotes.MonkaStab}",
                "boop" => $"{target} {Emotes.DarlBoop}",
                "laugh" => $"{Emotes.PepePoint} {target}", // TODO {Emotes.DarlClown}
                "hiton" => $"Come here often, {target}? {Emotes.DarlShyShy}",
                "uwu" => $"Daaayum! Look at {target} lookin' all cute and shit. {Emotes.KermitUwU}", // TODO {Emotes.DarlUwU}
                "beatup" => $"just beat up {target} {Emotes.EyesShaking}",
                _ => null
            };

            if (reply != null)
            {
                string prefix = ctx.Command switch
                {
                    "boop" => string.Empty,
                    "laugh" => string.Empty,
                    "hiton" => string.Empty,
                    "uwu" => string.Empty,
                    _ => $"{ctx.Author.Username} "
                };

                await ctx.ReplyAsync($"{prefix}{reply}");
            }
        }
    }
}
