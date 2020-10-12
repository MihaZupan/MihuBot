using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class Yogadesu : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        private readonly IConnectionMultiplexer _redis;

        public Yogadesu(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

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

                int count = (int)await _redis.GetDatabase().StringIncrementAsync("counters-yogadesu");
                count = Math.Min(count, 1024);

                await ctx.ReplyAsync($"Y{new string('o', count)}gades");
            }
        }
    }
}
