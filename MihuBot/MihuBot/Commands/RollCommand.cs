using MihuBot.Helpers;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class RollCommand : CommandBase
    {
        public override string Command => "roll";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            BigInteger sides = 6;

            if (ctx.Arguments.Length > 0 && BigInteger.TryParse(ctx.Arguments[0].Trim('d', 'D'), out BigInteger customSides))
                sides = customSides;

            string response;

            if (sides <= 0)
            {
                response = "No";
            }
            else if (sides <= 1_000_000_000)
            {
                response = (Rng.Next((int)sides) + 1).ToString();
            }
            else
            {
                double log10 = BigInteger.Log10(sides);
                if (log10 >= 64 * 1024)
                {
                    response = "42";
                }
                else
                {
                    byte[] bytes = new byte[(int)(log10 * 4)];
                    new Random().NextBytes(bytes);
                    BigInteger number = new BigInteger(bytes, true);

                    response = (number % sides).ToString();
                }
            }

            await ctx.ReplyAsync(response, mention: true);
        }
    }
}
