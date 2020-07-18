using Discord;
using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class FriendlyActionsCommands : CommandBase
    {
        public override string Command => "hug";
        public override string[] Aliases => new[] { "butt", "slap", "kick", "love", "kiss", "boop", "dropkickofftheturnbuckle" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (ctx.Guild.Id == Guilds.LiverGang && !(ctx.Command == "hug"))
            {
                return;
            }

            bool at = ctx.IsFromAdmin && ctx.Arguments.Length > 0 && ctx.Arguments[^1].Equals("at", StringComparison.OrdinalIgnoreCase);

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
                    rngUser = await ctx.Channel.GetUserAsync(userId);
                }
            }

            if (rngUser is null)
                rngUser = await ctx.Channel.GetRandomChannelUserAsync();

            string target = at ? MentionUtils.MentionUser(rngUser.Id) : rngUser.Username;

            bool targetIsAuthor = rngUser.Id == ctx.AuthorId;

            string reply = ctx.Command switch
            {
                "dropkickofftheturnbuckle" => $"drop kicked {target} off the turn buckle",
                "butt" => $"thinks {(targetIsAuthor ? "they have" : $"{target} has")} a nice butt! {Emotes.DarlBASS}",
                "slap" => $"just {(targetIsAuthor ? "performed a self-slap maneuver" : $"slapped {target}")}! {Emotes.MonkaHmm}",
                "kick" => $"just {(targetIsAuthor ? "tripped" : $"kicked {target}")}! {Emotes.DarlZoom}",
                "love" => $"wants {target} to know they are loved! {Emotes.DarlHearts}",
                "hug" => $"is {(targetIsAuthor ? "getting hugged" : $"sending hugs to {target}")}! {Emotes.SenpaiLove}",
                "kiss" => $"just kissed {target}! {Emotes.DarlKiss}",
                "boop" => $"{Emotes.DarlBoop}",
                _ => null
            };

            if (reply != null)
            {
                await ctx.ReplyAsync($"{ctx.Author.Username} {reply}");
            }
        }
    }
}
