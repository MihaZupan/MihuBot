namespace MihuBot.Commands
{
    public sealed class CustomMessageCommand : CommandBase
    {
        public override string Command => "message";
        public override string[] Aliases => new[] { "msg" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync("custommessage"))
                return;

            string[] lines = ctx.Content.Split('\n');
            string[] headers = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (headers.Length < 2)
            {
                await ctx.ReplyAsync("Missing command arguments, try ```\n!message channelId\nMessage text\n````");
                return;
            }

            if (!ulong.TryParse(headers[1], out ulong channelId))
            {
                await ctx.ReplyAsync("Invalid channel ID format");
                return;
            }

            SocketTextChannel channel = ctx.Discord.GetTextChannel(channelId);

            if (channel is null)
            {
                await ctx.ReplyAsync("Unknown channel.");
                return;
            }

            try
            {
                if (lines[1].AsSpan().TrimStart().StartsWith('{') && lines[^1].AsSpan().TrimEnd().EndsWith('}'))
                {
                    int first = ctx.Content.IndexOf('{');
                    int last = ctx.Content.LastIndexOf('}');
                    await EmbedHelper.SendEmbedAsync(ctx.Content.Substring(first, last - first + 1), channel);
                }
                else
                {
                    await channel.SendMessageAsync(ctx.Content.AsSpan(lines[0].Length + 1).Trim(stackalloc char[] { ' ', '\t', '\r', '\n' }).ToString());
                }
            }
            catch (Exception ex)
            {
                await ctx.ReplyAsync(ex.Message, mention: true);
                throw;
            }
        }
    }
}
