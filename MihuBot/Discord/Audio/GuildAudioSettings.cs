using Newtonsoft.Json;

namespace MihuBot.Discord.Audio;

public sealed class GuildAudioSettings
{
    [JsonIgnore]
    public SynchronizedLocalJsonStore<Dictionary<ulong, GuildAudioSettings>> UnderlyingStore;

    public float? Volume { get; set; }

    internal Task ChangeVolumeAsync(int percent, bool relative)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, relative ? -100 : 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);

        return ModifyAsync(static (settings, change) =>
        {
            float volume = change.Percent / 100f;
            settings.Volume = change.Relative
                ? Math.Clamp((settings.Volume ?? Constants.VCDefaultVolume) + volume, 0, 1)
                : volume;
        }, (Percent: percent, Relative: relative));
    }

    public async Task ModifyAsync<T>(Action<GuildAudioSettings, T> modificationAction, T state)
    {
        await UnderlyingStore.EnterAsync();
        try
        {
            modificationAction(this, state);
        }
        finally
        {
            UnderlyingStore.Exit();
        }
    }
}
