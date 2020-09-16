using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class PlayHandler : NonCommandHandler
    {
        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.IsMentioned && ctx.Content.Contains(" play ", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(async () => await OnPlayCommand(ctx));
            }

            return Task.CompletedTask;
        }

        private static async Task OnPlayCommand(MessageContext ctx)
        {
            var vc = ctx.Guild.VoiceChannels.FirstOrDefault(vc => vc.Users.Any(u => u.Id == ctx.AuthorId));

            AudioClient audioClient = null;

            try
            {
                audioClient = await AudioClient.TryGetOrJoinAsync(ctx.Guild, vc);
            }
            catch (Exception ex)
            {
                await ctx.DebugAsync(ex.ToString());
            }

            if (audioClient is null)
            {
                if (vc is null)
                {
                    await ctx.ReplyAsync("Join a VC first", mention: true);
                }
                else
                {
                    await ctx.ReplyAsync("Could not join channel", mention: true);
                }

                return;
            }

            await audioClient.TryQueueContentAsync(ctx.Message);
        }
    }
}
