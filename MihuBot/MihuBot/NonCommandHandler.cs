using System.Threading.Tasks;

namespace MihuBot
{
    public abstract class NonCommandHandler : CooldownTrackable
    {
        public abstract ValueTask HandleAsync(MessageContext ctx);

        public virtual Task InitAsync(ServiceCollection services) => Task.CompletedTask;
    }
}
