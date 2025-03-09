﻿namespace MihuBot.Commands;

public sealed class FlipCommand : CommandBase
{
    public override string Command => "flip";

    protected override int CooldownToleranceCount => 3;
    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(10);

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.ArgumentString.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 1)
        {
            var options = ctx.ArgumentString
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(o => o.Trim())
                .ToArray();

            const string ZeroWidthSpace = "​";

            string choice = options.FirstOrDefault(o => o.Contains(ZeroWidthSpace, StringComparison.Ordinal))
                ?? (options.Where(o => o.Contains("sleep", StringComparison.OrdinalIgnoreCase)).Count() == 1
                    ? options.Single(o => o.Contains("sleep", StringComparison.OrdinalIgnoreCase))
                    : null)
                ?? options.Random();

            choice = choice.Replace(ZeroWidthSpace, "").Trim();

            await ctx.ReplyAsync(choice, mention: true);
        }
        else if (ctx.Arguments.Length > 0 && int.TryParse(ctx.Arguments[0], out int count) && count > 0)
        {
            count = Math.Min(1024, count);
            int heads = Rng.FlipCoins(count);
            await ctx.ReplyAsync($"Heads: {heads}, Tails {count - heads}", mention: true);
        }
        else
        {
            await ctx.ReplyAsync(Rng.Bool() ? "Heads" : "Tails", mention: true);
        }
    }
}
