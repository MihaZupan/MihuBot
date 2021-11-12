namespace MihuBot.Commands
{
    public sealed class AvatarCommand : CommandBase
    {
        public override string Command => "avatar";
        public override string[] Aliases => new[] { "avatar2" };

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (await ChooseUserAsync(ctx) is not ulong userId || ctx.Guild.GetUser(userId) is not IGuildUser user)
            {
                await ctx.ReplyAsync("I don't know who that is");
                return;
            }

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

        private static async Task<ulong?> ChooseUserAsync(CommandContext ctx)
        {
            if (ctx.Arguments.Length == 0)
            {
                return ctx.AuthorId;
            }

            string pattern = ctx.Arguments[0];

            if (ctx.Message.MentionedUsers.Count != 0 && pattern.StartsWith("<@") && pattern.EndsWith('>'))
            {
                return Choose(ctx.Message.MentionedUsers).Id;
            }
                
            if (ulong.TryParse(pattern, out ulong userId))
            {
                return userId;
            }
            else
            {
                var channelUsers = (await ctx.Channel.GetUsersAsync().FlattenAsync()).ToArray();
                if (Choose(channelUsers, pattern) is { } channelUser)
                {
                    return channelUser.Id;
                }

                var guildUsers = (await ctx.Guild.GetUsersAsync().FlattenAsync()).ToArray();
                if (Choose(guildUsers, pattern) is { } guildUser)
                {
                    return guildUser.Id;
                }
            }

            return null;

            IUser Choose(IReadOnlyCollection<IUser> users, string pattern = null)
            {
                if (pattern is not null && users.Count > 1)
                {
                    users = users
                        .Where(u =>
                            u.Username.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                            (ctx.Guild.GetUser(u.Id)?.Nickname?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();
                }

                if (users.Count > 1)
                    users = users.Where(u => u.Id != ctx.AuthorId).ToList();

                if (users.Count > 1)
                    users = users.Where(u => u.Id != KnownUsers.Miha).ToList();

                return users.Count == 0 ? null : users.Random();
            }
        }
    }
}
