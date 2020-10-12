using Discord;
using MihuBot.Helpers;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.NonCommandHandlers
{
    public sealed class HusbandoAndWaifu : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        private readonly IConnectionMultiplexer _redis;

        public HusbandoAndWaifu(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            var content = ctx.Content;

            bool waifu = content.StartsWith("@waifu", StringComparison.OrdinalIgnoreCase);

            bool husbando = !waifu &&
                (content.StartsWith("@husband", StringComparison.OrdinalIgnoreCase) &&
                (content.Length == 8 || (content[8] | 0x20) == 'o'));

            return waifu || husbando
                ? HandleAsyncCore()
                : Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                string redisPrefix = waifu ? "waifu-" : "husbando-";

                bool all = waifu
                    ? content.StartsWith("@waifus", StringComparison.OrdinalIgnoreCase)
                    : (content.StartsWith("@husbands", StringComparison.OrdinalIgnoreCase) || content.StartsWith("@husbandos", StringComparison.OrdinalIgnoreCase));

                if (!await TryEnterOrWarnAsync(ctx))
                    return;

                IDatabase redis = _redis.GetDatabase();

                if (ctx.IsFromAdmin)
                {
                    string[] parts = content.Split(' ');
                    if (parts.Length > 2 && ulong.TryParse(parts[2], out ulong argId1))
                    {
                        ulong argId2;
                        switch (parts[1].ToLowerInvariant())
                        {
                            case "add":
                                if (parts.Length > 3 && ulong.TryParse(parts[3], out argId2))
                                {
                                    await redis.SetAddAsync(redisPrefix + argId1, argId2.ToString());
                                    return;
                                }
                                break;

                            case "remove":
                                if (parts.Length > 3 && ulong.TryParse(parts[3], out argId2))
                                {
                                    await redis.SetRemoveAsync(redisPrefix + argId1, argId2.ToString());
                                    return;
                                }
                                break;

                            case "list":
                                ulong[] partners = (await redis.SetScanAsync(redisPrefix + argId1).ToArrayAsync())
                                    .Select(p => ulong.Parse(p))
                                    .ToArray();
                                await ctx.ReplyAsync($"```\n{string.Join('\n', partners.Select(p => ctx.Discord.GetUser(p).Username))}\n```");
                                return;
                        }
                    }
                }

                RedisValue partner = await redis.SetRandomMemberAsync(redisPrefix + ctx.AuthorId);

                if (partner.IsNullOrEmpty || !ulong.TryParse(partner, out ulong partnerId))
                {
                    if (Rng.Bool())
                    {
                        await ctx.ReplyAsync(MentionUtils.MentionUser(KnownUsers.Jordan));
                    }
                    else
                    {
                        await ctx.ReplyAsync($"{Emotes.PepePoint}");
                    }
                }
                else if (!all)
                {
                    await ctx.ReplyAsync(MentionUtils.MentionUser(partnerId));
                }
                else
                {
                    ulong[] partners = (await redis.SetScanAsync(redisPrefix + ctx.AuthorId).ToArrayAsync())
                        .Select(p => ulong.Parse(p))
                        .ToArray();

                    await ctx.ReplyAsync(string.Join(' ', partners.Select(p => MentionUtils.MentionUser(p))));
                }
            }
        }
    }
}
