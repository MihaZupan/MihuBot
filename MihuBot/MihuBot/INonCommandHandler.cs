namespace MihuBot
{
    public interface INonCommandHandler
    {
        Task HandleAsync(MessageContext ctx);
    }
}
