namespace MihuBot.Commands
{
    public sealed class YellowCommand : CommandBase
    {
        public override string Command => "yellow";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.Author.SendMessageAsync("https://cdn.discordapp.com/attachments/750706594412757094/887520872393478204/Color-yellow.jpg");
        }
    }
}
