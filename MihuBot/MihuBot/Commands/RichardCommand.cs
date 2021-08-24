using Discord;
using MihuBot.Helpers;

namespace MihuBot.Commands
{
    public sealed class RichardCommand : CommandBase
    {
        public override string Command => "richard";

        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(5);

        protected override int CooldownToleranceCount => 0;

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.ReplyAsync(
                "Looking for a chill playlist to listen to on stream or while doing work or just chilling?\n" +
                $"Check out {MentionUtils.MentionUser(KnownUsers.Richard)}'s music on Spotify!\n" +
                "https://open.spotify.com/artist/3IBzSgE7kzwntsReEx5hDP?si=7OZ9neJDT4eBDYvSaYuOVg",
                suppressMentions: true);
        }
    }
}
