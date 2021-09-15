namespace MihuBot.Commands
{
    public sealed class YellowCommand : CommandBase
    {
        public override string Command => "yellow";
        public override string[] Aliases => new[] { "red" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            (string url, int number) =
                ctx.Command == "yellow" ? ("https://cdn.discordapp.com/attachments/750706594412757094/887520872393478204/Color-yellow.jpg", 4) :
                ctx.Command == "red" ? ("https://cdn.discordapp.com/attachments/750706594412757094/887522954739581009/blue.png", 1)
                : (null, 0);

            for (int i = 0; i < number; i++)
            {
                if (i != 0)
                {
                    await Task.Delay(1000);
                }

                await ctx.Author.SendMessageAsync(url);
            }
        }
    }
}
