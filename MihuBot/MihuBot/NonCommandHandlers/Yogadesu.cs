namespace MihuBot.NonCommandHandlers
{
    public sealed class Yogadesu : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        private readonly SynchronizedLocalJsonStore<Box<int>> _counter = new("Yogadesu.json");

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Content.StartsWith("@yogadesu", StringComparison.OrdinalIgnoreCase))
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                if (!await TryEnterOrWarnAsync(ctx))
                    return;

                int count;
                var counter = await _counter.EnterAsync();
                try
                {
                    count = ++counter.Value;
                }
                finally
                {
                    _counter.Exit();
                }

                count = Math.Min(count, 1024);
                await ctx.ReplyAsync($"Y{new string('o', count)}gades");
            }
        }
    }
}
