using Discord.Audio;
using System.Buffers;
using System.Runtime.InteropServices;

#nullable enable

namespace MihuBot.Discord.Audio;

public sealed class PcmAudioScheduler : IAsyncDisposable
{
    private const int DecreasedVolumeFramesAfterSilence = 300 / OpusConstants.FrameMillis;

    private readonly MediaTimer<byte[]> _timer;
    private readonly Action<string> _debugLog;
    private int _leftoverBytes;
    private byte[]? _leftoverBuffer;
    private int _framesAfterSilence;

    public GuildAudioSettings? AudioSettings { get; set; }

    public PcmAudioScheduler(IAudioClient audioClient, AudioOutStream pcmStream, Action<string> debugLog, Action<Exception> onError)
    {
        _debugLog = debugLog;

        _timer = new MediaTimer<byte[]>(
            framerate: 1000d / OpusConstants.FrameMillis,
            bufferSize: TimeSpan.FromMilliseconds(GlobalAudioSettings.StreamBufferMs),
            prolongedSilence: TimeSpan.FromMilliseconds(500),
            onError: onError)
        {
            OnFrameAsync = async frame =>
            {
                _debugLog("OnFrameAsync start");

                Task? speakingTask = null;
                try
                {
                    speakingTask = audioClient.SetSpeakingAsync(true);
                    Memory<byte> frameBytes = frame.AsMemory(0, OpusConstants.FrameBytes);

                    float userVolume = AudioSettings?.Volume ?? Constants.VCDefaultVolume;

                    int framesAfterSilence = ++_framesAfterSilence;
                    if (framesAfterSilence < DecreasedVolumeFramesAfterSilence)
                    {
                        userVolume *= 1f / DecreasedVolumeFramesAfterSilence * framesAfterSilence;
                    }

                    float volumeFactor = VolumeHelper.GetAmplitudeFactorForVolumeSlider(userVolume);

                    VolumeHelper.ApplyVolume(MemoryMarshal.Cast<byte, short>(frameBytes.Span), volumeFactor);

                    await pcmStream.WriteAsync(frameBytes);
                }
                finally
                {
                    // A timed-out transport callback still owns this buffer until its write finishes.
                    ArrayPool<byte>.Shared.Return(frame);

                    if (speakingTask is not null)
                    {
                        await speakingTask;
                    }

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

                try
                {
                    await _timer.EnqueueAsync(buffer, cancellationToken);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

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

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _timer.DisposeAsync();
        }
        finally
        {
            if (_leftoverBuffer is { } buffer)
            {
                _leftoverBuffer = null;
                _leftoverBytes = 0;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
