using Discord.Audio;
using System.Buffers;

namespace MihuBot.Audio;

public sealed class PcmAudioScheduler : IAsyncDisposable
{
    private readonly MediaTimer<byte[]> _timer;
    private int _leftoverBytes;
    private byte[]? _leftoverBuffer;

    public PcmAudioScheduler(IAudioClient audioClient, AudioOutStream pcmStream)
    {
        _timer = new MediaTimer<byte[]>(
            framerate: 1000d / OpusConstants.FrameMillis,
            bufferSize: TimeSpan.FromMilliseconds(GlobalAudioSettings.StreamBufferMs),
            prolongedSilence: TimeSpan.FromMilliseconds(500))
        {
            OnFrameAsync = async frame =>
            {
                await audioClient.SetSpeakingAsync(true);

                await pcmStream.WriteAsync(frame.AsMemory(0, OpusConstants.FrameBytes));

                ArrayPool<byte>.Shared.Return(frame);
            },
            OnProlongedSilenceAsync = async () =>
            {
                await audioClient.SetSpeakingAsync(false);
            }
        };
    }

    public async Task WritePcmSamplesAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        while (!pcm.IsEmpty)
        {
            byte[] buffer = _leftoverBuffer ??= ArrayPool<byte>.Shared.Rent(OpusConstants.FrameBytes);

            int toCopy = Math.Min(pcm.Length, OpusConstants.FrameBytes - _leftoverBytes);
            pcm.Span.Slice(0, toCopy).CopyTo(buffer.AsSpan(_leftoverBytes));
            pcm = pcm.Slice(toCopy);
            _leftoverBytes += toCopy;

            if (_leftoverBytes == OpusConstants.FrameBytes)
            {
                _leftoverBuffer = null;
                _leftoverBytes = 0;
                await _timer.EnqueueAsync(buffer, cancellationToken);
            }
        }
    }

    public ValueTask DisposeAsync() => _timer.DisposeAsync();
}
