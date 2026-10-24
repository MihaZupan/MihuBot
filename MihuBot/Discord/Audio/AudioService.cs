using System.Collections.Concurrent;

namespace MihuBot.Discord.Audio;

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
    private readonly ConcurrentDictionary<ulong, AudioPlayer> _audioPlayers;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _connectionLocks = new();
    private readonly Logger _logger;
    private readonly AudioHistoryService _history;
    private readonly AudioRecommendationService _recommendations;
    private readonly AudioSpeechService _speech;

    public AudioService(Logger logger, DiscordSocketClient discord, AudioHistoryService history, AudioRecommendationService recommendations,
        AudioSpeechService speech = null)
        : this(logger, new())
    {
        _history = history;
        _recommendations = recommendations;
        _speech = speech;

        discord.UserVoiceStateUpdated += (user, before, after) =>
        {
            if (user.Id == discord.CurrentUser?.Id && before.VoiceChannel is SocketVoiceChannel vc)
            {
                _ = HandleVoiceChannelChangedAsync(vc.Guild.Id, vc.Id, after.VoiceChannel?.Id);
            }

            return Task.CompletedTask;
        };
    }

    internal AudioService(Logger logger, ConcurrentDictionary<ulong, AudioPlayer> audioPlayers)
    {
        _logger = logger;
        _audioPlayers = audioPlayers;
    }

    internal async Task HandleVoiceChannelChangedAsync(ulong guildId, ulong beforeChannelId, ulong? afterChannelId)
    {
        if (beforeChannelId == afterChannelId ||
            !_audioPlayers.TryGetValue(guildId, out AudioPlayer player) ||
            player.VoiceChannel?.Id != beforeChannelId)
        {
            return;
        }

        try
        {
            await player.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.DebugLog($"Voice disconnect cleanup failed: {ex}", guildId: guildId);
        }
    }

    public bool TryGetAudioPlayer(ulong guildId, out AudioPlayer audioPlayer)
    {
        if (_audioPlayers.TryGetValue(guildId, out audioPlayer) && audioPlayer.IsReady)
        {
            return true;
        }

        audioPlayer = null;
        return false;
    }

    public ValueTask<AudioPlayer> GetOrCreateAudioPlayerAsync(DiscordSocketClient client, SocketTextChannel lastTextChannel, SocketVoiceChannel voiceChannel) =>
        GetOrCreateAudioPlayerAsync(voiceChannel.Guild.Id, async () =>
        {
            GuildAudioSettings audioSettings;
            var settings = await _audioSettings.EnterAsync();

            try
            {
                if (!settings.TryGetValue(voiceChannel.Guild.Id, out audioSettings))
                {
                    audioSettings = settings[voiceChannel.Guild.Id] = new()
                    {
                        UnderlyingStore = _audioSettings
                    };
                }
            }
            finally
            {
                _audioSettings.Exit();
            }

            return new AudioPlayer(client, audioSettings, _logger, voiceChannel.Guild, lastTextChannel,
                _audioPlayers, _history, _recommendations, _speech);
        }, player => player.JoinChannelAsync(voiceChannel));

    internal async ValueTask<AudioPlayer> GetOrCreateAudioPlayerAsync(ulong guildId, Func<Task<AudioPlayer>> create,
        Func<AudioPlayer, Task> join)
    {
        SemaphoreSlim connectionLock = _connectionLocks.GetOrAdd(guildId, static _ => new(1, 1));
        await connectionLock.WaitAsync();

        try
        {
            if (_audioPlayers.TryGetValue(guildId, out AudioPlayer audioPlayer))
            {
                if (audioPlayer.IsReady)
                {
                    return audioPlayer;
                }

                try
                {
                    await audioPlayer.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.DebugLog($"Previous voice connection cleanup failed: {ex}", guildId: guildId);
                }
            }

            audioPlayer = await create();
            _audioPlayers[guildId] = audioPlayer;

            try
            {
                await join(audioPlayer);
                return audioPlayer;
            }
            catch
            {
                try
                {
                    await audioPlayer.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.DebugLog($"Failed voice connection cleanup failed: {ex}", guildId: guildId);
                }

                throw;
            }
        }
        finally
        {
            connectionLock.Release();
        }
    }
}
