namespace MihuBot.NonCommandHandlers
{
    public class Civ : NonCommandHandler
    {
        private DateTime _lastSentMessage = DateTime.UtcNow.Subtract(TimeSpan.FromDays(5));

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Guild.Id == Guilds.TheBoys &&
                ctx.Content.Contains("civ", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Content.Contains("://", StringComparison.Ordinal))
            {
                lock (this)
                {
                    if (_lastSentMessage.Add(TimeSpan.FromDays(1)) > DateTime.UtcNow)
                    {
                        return Task.CompletedTask;
                    }

                    _lastSentMessage = DateTime.UtcNow;
                }

                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                await ctx.ReplyAsync("https://cdn.discordapp.com/emojis/818062423789142046.webp");
            }
        }
    }
}
