namespace MihuBot.Audio;

public interface IAudioSource : IAsyncDisposable
{
    void StartInitializing(int bitrateHintKbit);

    Task<bool> EnsureInitializedAsync();

    TimeSpan? Remaining { get; }

    string Description { get; }

    string Url { get; }

    string ThumbnailUrl { get; }

    SocketGuildUser Requester { get; }

    ValueTask<int> ReadAsync(Memory<byte> pcmBuffer, CancellationToken cancellationToken);

    void DebugDump(StringBuilder sb) { }
}