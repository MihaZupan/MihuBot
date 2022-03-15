namespace MihuBot.NonCommandHandlers
{
    public class Civ : NonCommandHandler
    {
        private readonly Stopwatch _lastSentMessage = Stopwatch.StartNew();

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Guild.Id == Guilds.TheBoys && ctx.Content.Contains("civ", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lastSentMessage)
                {
                    if (_lastSentMessage.Elapsed.TotalDays < 1)
                    {
                        return Task.CompletedTask;
                    }

                    _lastSentMessage.Restart();
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
