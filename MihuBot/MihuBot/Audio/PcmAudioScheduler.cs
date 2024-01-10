using Discord.Audio;
using System.Buffers;
using System.Runtime.InteropServices;

namespace MihuBot.Audio;

public sealed class PcmAudioScheduler : IAsyncDisposable
{
    private const int DecreasedVolumeFramesAfterSilence = 300 / OpusConstants.FrameMillis;

    private readonly MediaTimer<byte[]> _timer;
    private readonly Action<string> _debugLog;
    private int _leftoverBytes;
    private byte[]? _leftoverBuffer;
    private int _framesAfterSilence;

    public GuildAudioSettings AudioSettings { get; set; }

    public PcmAudioScheduler(IAudioClient audioClient, AudioOutStream pcmStream, Action<string> debugLog)
    {
        _debugLog = debugLog;

        _timer = new MediaTimer<byte[]>(
            framerate: 1000d / OpusConstants.FrameMillis,
            bufferSize: TimeSpan.FromMilliseconds(GlobalAudioSettings.StreamBufferMs),
            prolongedSilence: TimeSpan.FromMilliseconds(500))
        {
            OnFrameAsync = async frame =>
            {
                _debugLog("OnFrameAsync start");

                Task speakingTask = audioClient.SetSpeakingAsync(true);
                try
                {
                    Memory<byte> frameBytes = frame.AsMemory(0, OpusConstants.FrameBytes);

                    float rawVolume = AudioSettings?.Volume ?? Constants.VCDefaultVolume;

                    int framesAfterSilence = ++_framesAfterSilence;
                    if (framesAfterSilence < DecreasedVolumeFramesAfterSilence)
                    {
                        rawVolume *= 1f / DecreasedVolumeFramesAfterSilence * framesAfterSilence;
                    }

                    float volume = rawVolume * rawVolume;

                    VolumeHelper.ApplyVolume(MemoryMarshal.Cast<byte, short>(frameBytes.Span), volume);

                    await pcmStream.WriteAsync(frameBytes);

                    ArrayPool<byte>.Shared.Return(frame);
                }
                finally
                {
                    await speakingTask;

                    _debugLog("OnFrameAsync stop");
                }
            },
            OnSilenceFrameAsync = () =>
            {
                _debugLog("OnSilenceFrameAsync");
                return Task.CompletedTask;
            },
            OnProlongedSilenceAsync = async () =>
            {
                _debugLog("OnProlongedSilenceAsync");
                _framesAfterSilence = 0;
                await audioClient.SetSpeakingAsync(false);
            },
            OnClearedFrameAsync = frame =>
            {
                _debugLog("OnClearedFrameAsync");
                ArrayPool<byte>.Shared.Return(frame);
                return Task.CompletedTask;
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

                _debugLog("WritePcmSamplesAsync Enqueue start");

                await _timer.EnqueueAsync(buffer, cancellationToken);

                _debugLog("WritePcmSamplesAsync Enqueue stop");
            }
        }
    }

    public async Task ClearAsync()
    {
        _leftoverBytes = 0;
        _framesAfterSilence = 0;
        await _timer.ClearAsync();
    }

    public ValueTask DisposeAsync() => _timer.DisposeAsync();
}
