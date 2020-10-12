using System.Threading.Tasks;

namespace MihuBot
{
    public abstract class NonCommandHandler : CooldownTrackable, INonCommandHandler
    {
        public abstract Task HandleAsync(MessageContext ctx);

        public virtual Task InitAsync() => Task.CompletedTask;
    }
}
