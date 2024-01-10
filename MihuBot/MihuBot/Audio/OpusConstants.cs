namespace MihuBot.Audio;

internal static class OpusConstants
{
    public const int SamplingRate = 48000;
    public const int Channels = 2;
    public const int FrameMillis = 20;

    public const int SampleBytes = sizeof(short) * Channels;

    public const int FramesPerSecond = 1000 / FrameMillis;
    public const int FrameSamplesPerChannel = SamplingRate / 1000 * FrameMillis;
    public const int FrameSamples = FrameSamplesPerChannel * Channels;
    public const int FrameBytes = FrameSamplesPerChannel * SampleBytes;

    public const int BytesPerMs = FramesPerSecond * FrameBytes / 1000;
}
