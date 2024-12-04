using MihuBot.Configuration;

namespace MihuBot.NonCommandHandlers;

public sealed class JaredVoiceChannel : NonCommandHandler
{
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;
    private readonly CooldownTracker _renameCooldown = new(TimeSpan.FromMinutes(6), 1);

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

            if (user is null || before.VoiceChannel == after.VoiceChannel)
            {
                return;
            }

            if (after.VoiceChannel?.Id == ChannelId)
            {
                await ChangeNameAsync(after.VoiceChannel, user);
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
                string newName = $"{prefix} {KnownUsers.GetName(user)}";

                if (channel.Name != newName && _renameCooldown.TryEnter(42))
                {
                    await channel.ModifyAsync(props => props.Name = newName);
                }
            }
        }
    }
}
