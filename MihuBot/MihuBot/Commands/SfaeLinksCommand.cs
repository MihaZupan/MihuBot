namespace MihuBot.Commands
{
    public sealed class SfaeLinksCommand : CommandBase
    {
        private const string CommandName = "sfaelinks";
        private const string PlusOneButtonId = CommandName + "-plus-1";
        private const string MinusOneButtonId = CommandName + "-minus-1";
        private const string CloseButtonId = CommandName + "-close";

        public override string Command => CommandName;
        public override string[] Aliases => new[] { "sfaelink", "slg" };

        private readonly SynchronizedLocalJsonStore<Box<int>> _counter = new("SfaeLinks.json");

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            await ctx.Channel.SendMessageAsync(
                text: CurrentTally(await _counter.QueryAsync(i => i.Value)),
                components: new ComponentBuilder()
                    .WithButton("+1", PlusOneButtonId)
                    .WithButton("-1", MinusOneButtonId)
                    .WithButton("Close", CloseButtonId, ButtonStyle.Danger)
                    .Build());
        }

        public override async Task HandleMessageComponentAsync(SocketMessageComponent component)
        {
            if (component.User is null || component.User.Id == KnownUsers.Sfae)
            {
                return;
            }

            var counter = await _counter.EnterAsync();
            try
            {
                switch (component.Data.CustomId)
                {
                    case PlusOneButtonId:
                        counter.Value++;
                        break;

                    case MinusOneButtonId:
                        counter.Value--;
                        break;

                    case CloseButtonId:
                        await component.Message.DeleteAsync();
                        return;
                }

                await component.UpdateAsync(m => m.Content = CurrentTally(counter.Value));
            }
            finally
            {
                _counter.Exit();
            }
        }

        private static string CurrentTally(int num) => $"Current tally: {(num > 0 ? $"+{num}" : num.ToString())}";
    }
}
