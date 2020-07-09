using MihuBot.Helpers;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class AuthorReactions : NonCommandHandler
    {
        public override async ValueTask HandleAsync(MessageContext ctx)
        {
            var message = ctx.Message;

            if (ctx.AuthorId == KnownUsers.Gradravin && Rng.Chance(1000))
            {
                await message.AddReactionAsync(Emotes.DarlBoop);
            }

            if (ctx.AuthorId == KnownUsers.James && Rng.Chance(50))
            {
                await message.AddReactionAsync(Emotes.CreepyFace);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(750);
                        await message.RemoveReactionAsync(Emotes.CreepyFace, KnownUsers.MihuBot);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                });
            }
        }
    }
}
