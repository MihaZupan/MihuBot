namespace MihuBot.Commands
{
    public sealed class PollCommand : CommandBase
    {
        public override string Command => "poll";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync("poll"))
                return;

            string[] parts = StringHelpers.TrySplitQuotedArgumentString(ctx.ArgumentString, out string error);

            if (error != null)
            {
                await ctx.ReplyAsync("Invalid arguments format: " + error, mention: true);
                return;
            }

            if (parts.Length < 3)
            {
                await ctx.ReplyAsync("Need at least a title and 2 options", mention: true);
                return;
            }

            if (parts.Length > 10)
            {
                await ctx.ReplyAsync("I support at most 9 options", mention: true);
                return;
            }

            EmbedBuilder pollEmbed = new EmbedBuilder()
                .WithColor(r: 0, g: 255, b: 0)
                .WithAuthor(ctx.Author.GetName(), ctx.Author.GetAvatarUrl());

            StringBuilder embedValue = new StringBuilder();

            for (int i = 1; i < parts.Length; i++)
            {
                if (i != 1) embedValue.Append('\n');

                embedValue.Append(Constants.NumberEmojis[i]);
                embedValue.Append(' ');
                embedValue.Append(parts[i]);
            }

            pollEmbed.AddField(parts[0], embedValue.ToString());

            var pollMessage = await ctx.Channel.SendMessageAsync(embed: pollEmbed.Build());

            await pollMessage.AddReactionsAsync(
                Enumerable.Range(1, parts.Length - 1).Select(i => Constants.NumberEmotes[i]).ToArray());
        }
    }
}
