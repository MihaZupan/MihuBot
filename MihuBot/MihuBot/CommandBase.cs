namespace MihuBot
{
    public abstract class CommandBase : CooldownTrackable, INonCommandHandler
    {
        public abstract string Command { get; }

        public abstract Task ExecuteAsync(CommandContext ctx);

        public virtual string[] Aliases { get; } = Array.Empty<string>();

        public virtual Task InitAsync() => Task.CompletedTask;

        public virtual Task HandleAsync(MessageContext ctx) => Task.CompletedTask;

        public virtual Task HandleMessageComponentAsync(SocketMessageComponent component) => Task.CompletedTask;

        protected static string GetMessageLink(ulong guildId, ulong channelId, ulong messageId) =>
            $"https://discordapp.com/channels/{guildId}/{channelId}/{messageId}";
    }
}
