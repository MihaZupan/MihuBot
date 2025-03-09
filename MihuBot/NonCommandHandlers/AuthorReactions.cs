namespace MihuBot.NonCommandHandlers;

public sealed class AuthorReactions : NonCommandHandler
{
    public override Task HandleAsync(MessageContext ctx)
    {
        if (ctx.AuthorId == KnownUsers.James && Rng.Chance(50))
        {
            return HandleAsyncCore();
        }

        return Task.CompletedTask;

        async Task HandleAsyncCore()
        {
            var message = ctx.Message;

            if (ctx.AuthorId == KnownUsers.James)
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
