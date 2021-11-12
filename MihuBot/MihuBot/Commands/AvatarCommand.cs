namespace MihuBot.Commands
{
    public sealed class AvatarCommand : CommandBase
    {
        public override string Command => "avatar";
        public override string[] Aliases => new[] { "avatar2" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            IGuildUser user = await ChooseUserAsync(ctx) ?? ctx.Author;

            string guildAvatarId = user.GuildAvatarId;
            bool useAvatarId = guildAvatarId is null || ctx.Command == "avatar";
            string avatarId = useAvatarId ? user.AvatarId : guildAvatarId;

            ImageFormat format = avatarId is not null && avatarId.StartsWith("a_", StringComparison.Ordinal)
                ? ImageFormat.Gif
                : ImageFormat.Png;

            const int Size = 1024;

            string avatarUrl = useAvatarId
                ? (user.GetAvatarUrl(format, Size) ?? user.GetDefaultAvatarUrl())
                : user.GetGuildAvatarUrl(format, Size);

            await ctx.ReplyAsync(avatarUrl);
        }

        private static async Task<IGuildUser> ChooseUserAsync(CommandContext ctx)
        {
            if (ctx.Arguments.Length > 0)
            {
                string pattern = ctx.Arguments[0];

                if (ctx.Message.MentionedUsers.Count != 0 && pattern.StartsWith("<@") && pattern.EndsWith('>'))
                {
                    var mentioned = ctx.Message.MentionedUsers
                        .Select(m => ctx.Guild.GetUser(m.Id))
                        .Where(m => m is not null)
                        .ToArray();

                    if (mentioned.Length > 0)
                    {
                        return mentioned.Random();
                    }
                }
                
                if (ulong.TryParse(pattern, out ulong userId) && ctx.Guild.GetUser(userId) is { } guildUser)
                {
                    return guildUser;
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

                        return ctx.Guild.GetUser(matches.Random().Id);
                    }
                }
            }

            return null;
        }
    }
}
