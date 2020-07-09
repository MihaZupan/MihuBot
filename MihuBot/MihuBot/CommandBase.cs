using System;
using System.Threading.Tasks;

namespace MihuBot
{
    public abstract class CommandBase
    {
        public abstract string Command { get; }

        public abstract Task ExecuteAsync(CommandContext ctx);

        public virtual string[] Aliases { get; } = Array.Empty<string>();
    }
}
