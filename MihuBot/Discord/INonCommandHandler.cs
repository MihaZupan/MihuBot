namespace MihuBot.Discord;

public interface INonCommandHandler
{
    Task HandleAsync(MessageContext ctx);
}
