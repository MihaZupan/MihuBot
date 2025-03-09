using Newtonsoft.Json;

namespace MihuBot.Audio;

public sealed class GuildAudioSettings
{
    [JsonIgnore]
    public SynchronizedLocalJsonStore<Dictionary<ulong, GuildAudioSettings>> UnderlyingStore;

    public float? Volume { get; set; }

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
