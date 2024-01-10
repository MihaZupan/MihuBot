namespace MihuBot.Audio;

public static class GlobalAudioSettings
{
    public static int StreamBufferMs = 500;
    public static int PacketLoss = 5;
    public static int MinBitrateKb = 96;
    public static int MinBitrate => MinBitrateKb * 1000;
    public static int MaxBitrate => 128 * 1000;
}