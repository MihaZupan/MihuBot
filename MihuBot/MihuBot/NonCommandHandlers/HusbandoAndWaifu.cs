using MihuBot.Husbando;

namespace MihuBot.NonCommandHandlers
{
    public sealed class HusbandoAndWaifu : NonCommandHandler
    {
        protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);

        private readonly IHusbandoService _husbandoService;

        public HusbandoAndWaifu(IHusbandoService husbandoService)
        {
            _husbandoService = husbandoService;
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
                bool all = waifu
                    ? content.StartsWith("@waifus", StringComparison.OrdinalIgnoreCase)
                    : (content.StartsWith("@husbands", StringComparison.OrdinalIgnoreCase) || content.StartsWith("@husbandos", StringComparison.OrdinalIgnoreCase));

                if (!await TryEnterOrWarnAsync(ctx))
                    return;

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
                                    await _husbandoService.AddMatchAsync(husbando, argId1, argId2);
                                    return;
                                }
                                break;

                            case "remove":
                                if (parts.Length > 3 && ulong.TryParse(parts[3], out argId2))
                                {
                                    await _husbandoService.RemoveMatchAsync(husbando, argId1, argId2);
                                    return;
                                }
                                break;

                            case "list":
                                ulong[] partners = await _husbandoService.GetAllMatchesAsync(husbando, argId1);
                                await ctx.ReplyAsync($"```\n{string.Join('\n', partners.Select(p => ctx.Discord.GetUser(p).GetName()))}\n```");
                                return;
                        }
                    }
                }

                ulong? partner = await _husbandoService.TryGetRandomMatchAsync(husbando, ctx.AuthorId);

                if (!partner.HasValue)
                {
                    if (Rng.Bool())
                    {
                        await ctx.ReplyAsync(MentionUtils.MentionUser(KnownUsers.Jordan), suppressMentions: true);
                    }
                    else
                    {
                        await ctx.ReplyAsync($"{Emotes.PepePoint}");
                    }
                }
                else if (!all)
                {
                    await ctx.ReplyAsync(MentionUtils.MentionUser(partner.Value));
                }
                else
                {
                    ulong[] partners = await _husbandoService.GetAllMatchesAsync(husbando, ctx.AuthorId);
                    await ctx.ReplyAsync(string.Join(' ', partners.Select(p => MentionUtils.MentionUser(p))));
                }
            }
        }
    }
}
