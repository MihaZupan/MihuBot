using Discord;
using MihuBot.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class AtVoiceChat : INonCommandHandler
    {
        public Task HandleAsync(MessageContext ctx)
        {
            if (ctx.IsFromAdmin &&
                ctx.Content.StartsWith('@') &&
                ulong.TryParse(ctx.Content.AsSpan(1), out ulong id) &&
                ctx.Guild.VoiceChannels.TryGetFirst(id, out var vc))
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                if (vc.Users.Count > 0)
                {
                    string message = string.Join(' ', vc.Users.Select(u => MentionUtils.MentionUser(u.Id)));
                    await ctx.ReplyAsync(message);
                }
            }
        }
    }
}
