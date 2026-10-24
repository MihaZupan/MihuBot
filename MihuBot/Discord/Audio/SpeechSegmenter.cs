using System.Buffers.Binary;
using System.Runtime.InteropServices;

#nullable enable

namespace MihuBot.Discord.Audio;

internal sealed record SpeechSegment(
    float[] Samples, long StartSample, long EndSample, long Timestamp, long SpeechStartTimestamp, long SpeechEndTimestamp,
    bool IsContinuation = false, bool IsFinal = true);

internal sealed class SpeechSegmenter(Action<SpeechSegment> onSegment)
{
    internal const int SampleRate = 16_000;
    internal const int MaxSamples = SampleRate * 12;
    internal const int OverlapSamples = SampleRate * 2;
    private const int FrameSamples = SampleRate / 50;
    private const int PreRollSamples = SampleRate / 5;
    private const int MinSpeechSamples = SampleRate / 5;
    private const int SilenceSamples = SampleRate * 6 / 5;
    private const float SpeechThreshold = 0.01f;
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMilliseconds(1200);

    private readonly float[] _frame = new float[FrameSamples];
    private readonly float[] _preRoll = new float[PreRollSamples];
    private readonly List<float> _speech = [];
    private int _frameCount;
    private int _preRollCount;
    private int _preRollPosition;
    private int _speechSamples;
    private int _silenceSamples;
    private int _resampleSum;
    private int _resampleCount;
    private long _samplesReceived;
    private long _lastPacket;
    private long _speechStart;
    private long _speechStartTimestamp;
    private long _speechEndTimestamp;
    private bool _isContinuation;

    public void AddPcm(ReadOnlySpan<byte> pcm, long timestamp)
    {
        if (pcm.Length % 4 != 0)
        {
            throw new InvalidDataException("Expected 48 kHz stereo PCM16 voice audio.");
        }

        FlushIfIdle(timestamp);
        _lastPacket = timestamp;

        // Average stereo channels and three 48 kHz samples to produce 16 kHz mono.
        for (int i = 0; i < pcm.Length; i += sizeof(short))
        {
            _resampleSum += BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i, sizeof(short)));

            if (++_resampleCount == 6)
            {
                _frame[_frameCount++] = _resampleSum / (6f * 32768);
                _resampleSum = 0;
                _resampleCount = 0;
                _samplesReceived++;

                if (_frameCount == FrameSamples)
                {
                    ProcessFrame(timestamp);
                    _frameCount = 0;
                }
            }
        }
    }

    public void FlushIfIdle(long timestamp)
    {
        if (_lastPacket != 0 && Stopwatch.GetElapsedTime(_lastPacket, timestamp) >= SilenceTimeout)
        {
            Finish(timestamp, continuation: false);
            _frameCount = 0;
            _resampleCount = 0;
            _resampleSum = 0;
            _preRollCount = 0;
            _preRollPosition = 0;
            _lastPacket = 0;
        }
    }

    private void ProcessFrame(long timestamp)
    {
        double energy = 0;

        foreach (float sample in _frame)
        {
            energy += sample * sample;
        }

        bool voiced = energy / FrameSamples >= SpeechThreshold * SpeechThreshold;

        if (_speech.Count == 0 && voiced)
        {
            _speechStart = _samplesReceived - FrameSamples - _preRollCount;
            _speechStartTimestamp = timestamp;

            for (int i = 0; i < _preRollCount; i++)
            {
                _speech.Add(_preRoll[(_preRollPosition - _preRollCount + i + PreRollSamples) % PreRollSamples]);
            }
        }

        if (_speech.Count > 0 || voiced)
        {
            if (voiced)
            {
                _speechEndTimestamp = timestamp;
            }

            _speech.AddRange(_frame);
            _speechSamples += voiced ? FrameSamples : 0;
            _silenceSamples = voiced ? 0 : _silenceSamples + FrameSamples;

            if (_silenceSamples >= SilenceSamples)
            {
                Finish(timestamp, continuation: false);
            }
            else if (_speech.Count >= MaxSamples)
            {
                Finish(timestamp, continuation: true);
            }
        }

        foreach (float sample in _frame)
        {
            _preRoll[_preRollPosition] = sample;
            _preRollPosition = (_preRollPosition + 1) % PreRollSamples;
            _preRollCount = Math.Min(_preRollCount + 1, PreRollSamples);
        }
    }

    private void Finish(long timestamp, bool continuation)
    {
        if (_speechSamples >= MinSpeechSamples)
        {
            // Whisper needs at least a short input window, even for a clipped utterance.
            float[] samples = new float[Math.Max(SampleRate, _speech.Count)];
            CollectionsMarshal.AsSpan(_speech).CopyTo(samples);
            onSegment(new(samples, _speechStart, _speechStart + _speech.Count, timestamp, _speechStartTimestamp, _speechEndTimestamp,
                _isContinuation, IsFinal: !continuation));
        }

        if (continuation)
        {
            int remove = _speech.Count - OverlapSamples;
            _speech.RemoveRange(0, remove);
            _speechStart += remove;
            _speechStartTimestamp = timestamp - (long)((double)OverlapSamples / SampleRate * Stopwatch.Frequency);
        }
        else
        {
            _speech.Clear();
            _speechStartTimestamp = 0;
            _speechEndTimestamp = 0;
        }

        _speechSamples = 0;
        _silenceSamples = 0;
        _isContinuation = continuation;
    }
}
