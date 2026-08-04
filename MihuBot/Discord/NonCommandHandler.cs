namespace MihuBot.Discord;

public abstract class NonCommandHandler : CooldownTrackable, INonCommandHandler
{
    public abstract Task HandleAsync(MessageContext ctx);

    public virtual Task InitAsync() => Task.CompletedTask;
}
