using Discord;
using MihuBot.Helpers;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class BeatupMooCommand : CommandBase
    {
        public override string Command => "beatupmoo";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (ctx.AuthorId != KnownUsers.Moo)
            {
                await ctx.ReplyAsync($"{MentionUtils.MentionUser(KnownUsers.Moo)} just got beat up {Emotes.PepePoint}", suppressMentions: true);
            }
        }
    }
}
