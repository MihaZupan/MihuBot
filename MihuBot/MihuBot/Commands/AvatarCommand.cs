namespace MihuBot.Commands
{
    public sealed class AvatarCommand : CommandBase
    {
        public override string Command => "avatar";
        public override string[] Aliases => new[] { "banner" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            IUser user = await ChooseUserAsync(ctx) ?? ctx.Author;

            if (ctx.Command == "avatar")
            {
                string avatarId = user.AvatarId ?? user.GetDefaultAvatarUrl();

                ImageFormat format = avatarId.StartsWith("a_", StringComparison.Ordinal)
                    ? ImageFormat.Gif
                    : ImageFormat.Png;

                await ctx.ReplyAsync(user.GetAvatarUrl(format, size: 1024));
            }
            else if (ctx.Command == "banner")
            {
                if (user.BannerId is string bannerId)
                {
                    ImageFormat format = bannerId.StartsWith("a_", StringComparison.Ordinal)
                        ? ImageFormat.Gif
                        : ImageFormat.Png;

                    await ctx.ReplyAsync(user.GetBannerUrl(format, size: 1024));
                }
            }
        }

        private static async Task<IUser> ChooseUserAsync(CommandContext ctx)
        {
            if (ctx.Arguments.Length > 0)
            {
                string pattern = ctx.Arguments[0];

                if (ctx.Message.MentionedUsers.Count != 0 && pattern.StartsWith("<@") && pattern.EndsWith('>'))
                {
                    return ctx.Message.MentionedUsers.Random();
                }
                else if (ulong.TryParse(pattern, out ulong userId))
                {
                    return ctx.Discord.GetUser(userId);
                }
                else
                {
                    var users = await ctx.Channel.GetUsersAsync().ToArrayAsync();
                    var matches = users
                        .SelectMany(i => i)
                        .Where(u =>
                            u.Username.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                            (ctx.Guild.GetUser(u.Id)?.Nickname?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToArray();

                    if (matches.Length > 1)
                        matches = matches.Where(u => u.Id != ctx.AuthorId).ToArray();

                    if (matches.Length > 1)
                        matches = matches.Where(u => u.Id != KnownUsers.Miha).ToArray();

                    if (matches.Length > 0)
                    {
                        if (matches.Length > 1)
                        {
                            var closerMatches = matches
                                .Where(u =>
                                    u.Username.Split().Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                                    (ctx.Guild.GetUser(u.Id)?.Nickname?.Split().Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false))
                                .ToArray();

                            if (closerMatches.Length > 0)
                                matches = closerMatches;
                        }

                        return matches.Random();
                    }
                }
            }

            return null;
        }
    }
}
