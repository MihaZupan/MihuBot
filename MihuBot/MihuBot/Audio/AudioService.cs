using System.Collections.Concurrent;

namespace MihuBot.Audio;

public sealed class AudioService
{
    private readonly SynchronizedLocalJsonStore<Dictionary<ulong, GuildAudioSettings>> _audioSettings = new("AudioSettings.json", static (store, settings) =>
    {
        foreach (GuildAudioSettings guildSettings in settings.Values)
        {
            guildSettings.UnderlyingStore = store;
        }
        return settings;
    });
    private readonly ConcurrentDictionary<ulong, AudioPlayer> _audioPlayers = new();
    private readonly Logger _logger;

    public AudioService(Logger logger, DiscordSocketClient discord)
    {
        _logger = logger;

        discord.UserVoiceStateUpdated += (user, before, after) =>
        {
            if (user.Id == discord.CurrentUser.Id &&
                before.VoiceChannel is SocketVoiceChannel vc &&
                TryGetAudioPlayer(vc.Guild.Id, out AudioPlayer player))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (after.VoiceChannel is null)
                        {
                            await player.DisposeAsync();
                        }
                        else
                        {
                            // Moved between calls?
                            await player.DisposeAsync();

                            await after.VoiceChannel.DisconnectAsync();
                        }
                    }
                    catch { }
                });
            }
            return Task.CompletedTask;
        };
    }

    public bool TryGetAudioPlayer(ulong guildId, out AudioPlayer audioPlayer)
    {
        return _audioPlayers.TryGetValue(guildId, out audioPlayer);
    }

    public async ValueTask<AudioPlayer> GetOrCreateAudioPlayerAsync(DiscordSocketClient client, SocketTextChannel lastTextChannel, SocketVoiceChannel voiceChannel)
    {
        ulong guildId = voiceChannel.Guild.Id;

        while (true)
        {
            if (TryGetAudioPlayer(guildId, out AudioPlayer audioPlayer))
            {
                return audioPlayer;
            }

            GuildAudioSettings audioSettings;
            var settings = await _audioSettings.EnterAsync();
            try
            {
                if (!settings.TryGetValue(guildId, out audioSettings))
                {
                    audioSettings = settings[guildId] = new()
                    {
                        UnderlyingStore = _audioSettings
                    };
                }
            }
            finally
            {
                _audioSettings.Exit();
            }

            audioPlayer = new AudioPlayer(client, audioSettings, _logger, voiceChannel.Guild, lastTextChannel, _audioPlayers);
            if (_audioPlayers.TryAdd(guildId, audioPlayer))
            {
                await audioPlayer.JoinChannelAsync(voiceChannel);
                return audioPlayer;
            }
            else
            {
                await audioPlayer.DisposeAsync();
            }
        }
    }
}
