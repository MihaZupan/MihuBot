using MihuBot.Configuration;

namespace MihuBot.NonCommandHandlers;

public sealed class DeadIsSus : NonCommandHandler
{
    private readonly DiscordSocketClient _discord;
    private readonly IConfigurationService _configurationService;

    public DeadIsSus(DiscordSocketClient discord, IConfigurationService configurationService)
    {
        _discord = discord ?? throw new ArgumentNullException(nameof(discord));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
    }

    public override Task HandleAsync(MessageContext ctx) => Task.CompletedTask;

    public override Task InitAsync()
    {
        const ulong ComfyZone = 896228116001882132ul;
        const ulong DeafIsSus = 911422192917573692ul;

        _discord.UserVoiceStateUpdated += async (user, before, after) =>
        {
            if (after.IsSelfDeafened &&
                !user.IsBot &&
                after.VoiceChannel?.Id == ComfyZone &&
                _discord.GetGuild(Guilds.TheBoys) is { } guild &&
                guild.GetUser(user.Id) is { } guildUser &&
                guild.GetVoiceChannel(DeafIsSus) is { } deafIsSus &&
                _configurationService.TryGet(null, $"{nameof(DeafIsSus)}.Enabled", out var enabledString) &&
                bool.TryParse(enabledString, out var enabled) && enabled)
            {
                await guild.MoveAsync(guildUser, deafIsSus);
            }
        };

        return Task.CompletedTask;
    }
}
