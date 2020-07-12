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

            string reply;

            if (ctx.Command == "dropkickofftheturnbuckle")
            {
                reply = $" {ctx.Author.Username} drop kicked {target} off the turn buckle";
            }
            else if (ctx.Command == "butt")
            {
                reply = $"{ctx.Author.Username} thinks {(targetIsAuthor ? "they have" : $"{target} has")} a nice butt! {Emotes.DarlBASS}";
            }
            else if (ctx.Command == "slap")
            {
                reply = $"{ctx.Author.Username} just {(targetIsAuthor ? "performed a self-slap maneuver" : $"slapped {target}")}! {Emotes.MonkaHmm}";
            }
            else if (ctx.Command == "kick")
            {
                reply = $"{ctx.Author.Username} just {(targetIsAuthor ? "tripped" : $"kicked {target}")}! {Emotes.DarlZoom}";
            }
            else if (ctx.Command == "love")
            {
                reply = $"{ctx.Author.Username} wants {target} to know they are loved! {Emotes.DarlHearts}";
            }
            else if (ctx.Command == "hug")
            {
                reply = $"{ctx.Author.Username} is {(targetIsAuthor ? "getting hugged" : $"sending hugs to {target}")}! {Emotes.SenpaiComfy}";
            }
            else if (ctx.Command == "kiss")
            {
                reply = $"{ctx.Author.Username} just kissed {target}! {Emotes.DarlKiss}";
            }
            else if (ctx.Command == "boop")
            {
                reply = $"{target} {Emotes.DarlBoop}";
            }
            else return;

            await ctx.ReplyAsync(reply);
        }
    }
}
