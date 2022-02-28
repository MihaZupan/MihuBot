namespace MihuBot.NonCommandHandlers
{
    public sealed class DeadIsSus : NonCommandHandler
    {
        private readonly DiscordSocketClient _discord;

        public DeadIsSus(DiscordSocketClient discord)
        {
            _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        }

        public override Task HandleAsync(MessageContext ctx) => Task.CompletedTask;

        public override Task InitAsync()
        {
            const ulong ComfyZone = 896228116001882132ul;
            const ulong DeafIsSus = 911422192917573692ul;

            _discord.UserVoiceStateUpdated += async (user, before, after) =>
            {
                if (!before.IsSelfDeafened &&
                    after.IsSelfDeafened &&
                    !user.IsBot &&
                    _discord.GetGuild(Guilds.TheBoys) is { } guild &&
                    guild.GetVoiceChannel(ComfyZone) is { } comfyZone &&
                    comfyZone.GetUser(user.Id) is { } voiceUser &&
                    guild.GetVoiceChannel(DeafIsSus) is { } deafIsSus)
                {
                    await guild.MoveAsync(voiceUser, deafIsSus);
                }
            };

            return Task.CompletedTask;
        }
    }
}
