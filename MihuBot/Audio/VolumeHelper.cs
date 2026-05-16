using System.Runtime.Intrinsics;

namespace MihuBot.Audio;

internal static class VolumeHelper
{
    public static float GetAmplitudeFactorForVolumeSlider(float volume)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volume);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, 1);

        return volume * volume;
    }

    public static float GetVolumeAsDecibels(float volume)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volume);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, 1);

        return 20 * MathF.Log10(volume);
    }

    public static float GetVolumeSliderForDecibels(float decibels)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decibels, 0);

        float amplitudeFactor = MathF.Pow(10, decibels / 20);
        return MathF.Sqrt(amplitudeFactor);
    }

    public static void ApplyVolume(Span<short> pcm, float factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(factor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(factor, 1);

        if (factor == 1)
        {
            return;
        }

        Multiply(pcm, factor);
    }

    private static void Multiply(Span<short> span, float factor)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> factorVec = Vector256.Create(factor);

            while (span.Length >= Vector256<short>.Count)
            {
                Vector256<short> source = Vector256.Create(span);
                (Vector256<int> wideLow, Vector256<int> wideHigh) = Vector256.Widen(source);
                Vector256<float> singleLow = Vector256.ConvertToSingle(wideLow);
                Vector256<float> singleHigh = Vector256.ConvertToSingle(wideHigh);
                singleLow *= factorVec;
                singleHigh *= factorVec;
                wideLow = Vector256.ConvertToInt32(singleLow);
                wideHigh = Vector256.ConvertToInt32(singleHigh);
                Vector256<short> result = Vector256.Narrow(wideLow, wideHigh);
                result.CopyTo(span);
                span = span.Slice(Vector256<short>.Count);
            }
        }

        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (short)(span[i] * factor);
        }
    }
}
