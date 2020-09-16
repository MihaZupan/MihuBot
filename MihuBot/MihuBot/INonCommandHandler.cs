using System.Threading.Tasks;

namespace MihuBot
{
    public interface INonCommandHandler
    {
        Task HandleAsync(MessageContext ctx);
    }
}
