using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class ImperialToNormalCommand : CommandBase
    {
        public override string Command => "imperialtonormal";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            var fahrenheit = Regex.Match(ctx.ArgumentString, @"^(\d+?) ?f$", RegexOptions.IgnoreCase);
            if (fahrenheit.Success && double.TryParse(fahrenheit.Groups[1].Value, out double value))
            {
                double celsius = (value - 32) / 1.8d;
                await ctx.ReplyAsync($"{(int)celsius} °C");
            }
        }
    }
}
