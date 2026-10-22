namespace MihuBot.Discord.Audio;

public interface IAudioSource : IAsyncDisposable
{
    void StartInitializing(int bitrateHintKbit);

    Task<bool> EnsureInitializedAsync();

    TimeSpan? Remaining { get; }

    TimeSpan? Duration { get; }

    string Description { get; }

    string Url { get; }

    string ThumbnailUrl { get; }

    SocketGuildUser Requester { get; }

    bool IsAutoplay { get; }

    ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);

    void DebugDump(StringBuilder sb) { }
}