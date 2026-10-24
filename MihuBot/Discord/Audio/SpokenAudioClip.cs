using System.Buffers;
using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

#nullable enable

namespace MihuBot.Discord.Audio;

internal sealed class SpokenAudioClip
{
    internal const int MaxSamples = SpeechSegmenter.SampleRate * 20;
    private readonly SpeechSegment[] _segments;

    public string Transcript { get; }
    public long Timestamp => _segments[^1].Timestamp;

    private SpokenAudioClip(SpeechSegment[] segments, string transcript)
    {
        _segments = segments;
        Transcript = transcript;
    }

    public static SpokenAudioClip? Create(IReadOnlyList<SpeechSegment> segments, string transcript)
    {
        if (segments.Count == 0 || !segments[^1].IsFinal)
        {
            return null;
        }

        long start = segments[0].StartSample;
        long end = segments.Max(segment => segment.EndSample);

        if (end <= start || end - start > MaxSamples ||
            segments.Any(segment => segment.StartSample < start || segment.EndSample <= segment.StartSample ||
                segment.EndSample - segment.StartSample > segment.Samples.Length))
        {
            return null;
        }

        return new(segments.ToArray(), transcript);
    }

    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int Channels = 1;
    private const short PcmAudioFormat = 1;
    private const int FmtChunkSize = 16;
    private const int RiffHeaderSize = 12; // "RIFF" + chunk size + "WAVE"
    private const int FmtChunkHeaderSize = 8; // "fmt " + chunk size
    private const int DataChunkHeaderSize = 8; // "data" + chunk size
    private const int HeaderSize = RiffHeaderSize + FmtChunkHeaderSize + FmtChunkSize + DataChunkHeaderSize;
    private const int TagSize = 4; // Length of each four-character RIFF tag ("RIFF", "WAVE", "fmt ", "data").
    private const float FullScaleAmplitude = 1 << BitsPerSample >> 1; // 32768, the magnitude of short.MinValue.

    public byte[] ToWav()
    {
        long start = _segments[0].StartSample;
        int sampleCount = checked((int)(_segments.Max(segment => segment.EndSample) - start));
        int dataSize = sampleCount * BytesPerSample;

        byte[] wav = new byte[HeaderSize + dataSize];
        WriteHeader(wav, dataSize);
        Span<byte> data = wav.AsSpan(HeaderSize);

        float[] scratchFloats = ArrayPool<float>.Shared.Rent(sampleCount);
        short[] scratchShorts = ArrayPool<short>.Shared.Rent(sampleCount);
        try
        {
            foreach (SpeechSegment segment in _segments)
            {
                int offset = checked((int)(segment.StartSample - start));
                int length = checked((int)(segment.EndSample - segment.StartSample));

                ReadOnlySpan<float> samples = segment.Samples.AsSpan(0, length);
                Span<float> scaled = scratchFloats.AsSpan(0, length);
                Span<short> pcm = scratchShorts.AsSpan(0, length);

                if (!TensorPrimitives.IsFiniteAll(samples))
                {
                    throw new InvalidDataException("The request audio contains invalid samples.");
                }

                TensorPrimitives.Clamp(samples, -1f, 1f, scaled);
                TensorPrimitives.Multiply(scaled, FullScaleAmplitude, scaled);
                TensorPrimitives.Round(scaled, scaled);
                TensorPrimitives.ConvertSaturating<float, short>(scaled, pcm);

                Span<byte> destination = data.Slice(offset * BytesPerSample, length * BytesPerSample);

                if (BitConverter.IsLittleEndian)
                {
                    MemoryMarshal.AsBytes(pcm).CopyTo(destination);
                }
                else
                {
                    BinaryPrimitives.ReverseEndianness(pcm, MemoryMarshal.Cast<byte, short>(destination));
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratchFloats);
            ArrayPool<short>.Shared.Return(scratchShorts);
        }

        return wav;
    }

    // Writes the canonical 44-byte PCM WAV header describing `dataSize` bytes of mono 16-bit samples.
    private static void WriteHeader(Span<byte> wav, int dataSize)
    {
        const int BlockAlign = Channels * BytesPerSample;

        int pos = 0;

        pos += WriteAscii(wav, pos, "RIFF"u8);
        pos += WriteInt32(wav, pos, HeaderSize + dataSize - TagSize - sizeof(int)); // RIFF chunk size: file size minus the "RIFF" tag and this field.
        pos += WriteAscii(wav, pos, "WAVE"u8);

        pos += WriteAscii(wav, pos, "fmt "u8);
        pos += WriteInt32(wav, pos, FmtChunkSize);
        pos += WriteInt16(wav, pos, PcmAudioFormat);
        pos += WriteInt16(wav, pos, Channels);
        pos += WriteInt32(wav, pos, SpeechSegmenter.SampleRate);
        pos += WriteInt32(wav, pos, SpeechSegmenter.SampleRate * BlockAlign);
        pos += WriteInt16(wav, pos, BlockAlign);
        pos += WriteInt16(wav, pos, BitsPerSample);

        pos += WriteAscii(wav, pos, "data"u8);
        pos += WriteInt32(wav, pos, dataSize);

        Debug.Assert(pos == HeaderSize);
    }

    private static int WriteAscii(Span<byte> wav, int pos, ReadOnlySpan<byte> ascii)
    {
        ascii.CopyTo(wav.Slice(pos));
        return ascii.Length;
    }

    private static int WriteInt32(Span<byte> wav, int pos, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(wav.Slice(pos), value);
        return sizeof(int);
    }

    private static int WriteInt16(Span<byte> wav, int pos, int value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(wav.Slice(pos), (short)value);
        return sizeof(short);
    }
}
