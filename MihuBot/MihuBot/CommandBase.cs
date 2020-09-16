using System;
using System.Threading.Tasks;

namespace MihuBot
{
    public abstract class CommandBase : CooldownTrackable, INonCommandHandler
    {
        public abstract string Command { get; }

        public abstract Task ExecuteAsync(CommandContext ctx);

        public virtual string[] Aliases { get; } = Array.Empty<string>();

        public virtual Task InitAsync(ServiceCollection services) => Task.CompletedTask;

        public virtual Task HandleAsync(MessageContext ctx) => Task.CompletedTask;
    }
}
