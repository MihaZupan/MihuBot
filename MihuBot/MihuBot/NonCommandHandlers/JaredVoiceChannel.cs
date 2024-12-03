using MihuBot.Configuration;

namespace MihuBot.NonCommandHandlers;

public sealed class JaredVoiceChannel : NonCommandHandler
{
    protected override TimeSpan Cooldown => TimeSpan.FromMinutes(1);
    protected override int CooldownToleranceCount => 3;

    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;

    public JaredVoiceChannel(InitializedDiscordClient discord, Logger logger, IConfigurationService configuration)
    {
        _logger = logger;
        _configuration = configuration;

        discord.UserVoiceStateUpdated += HandleUserVoiceStateUpdatedAsync;
    }

    public override Task HandleAsync(MessageContext ctx) => Task.CompletedTask;

    private async Task HandleUserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        try
        {
            const ulong ChannelId = 1301957878164226068;

            if (user is null || !TryPeek(user.Id))
            {
                return;
            }

            if (after.VoiceChannel?.Id == ChannelId && TryEnter(user.Id))
            {
                await ChangeNameAsync(after.VoiceChannel, user);
            }
            else if (before.VoiceChannel?.Id == ChannelId && TryEnter(user.Id))
            {
                await ChangeNameAsync(before.VoiceChannel, user);
            }
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"{ex}");
        }

        async Task ChangeNameAsync(SocketVoiceChannel channel, SocketUser user)
        {
            if (_configuration.TryGet(null, "JaredVoiceChannelPrefix", out string prefix))
            {
                await channel.ModifyAsync(props => props.Name = $"{prefix} {KnownUsers.GetName(user)}");
            }
        }
    }
}
