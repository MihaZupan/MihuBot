using System.Runtime.Intrinsics;

namespace MihuBot.Discord.Audio;

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
        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<float> factorVec = Vector512.Create(factor);

            while (span.Length >= Vector512<short>.Count)
            {
                Vector512<short> source = Vector512.Create(span);
                (Vector512<int> wideLow, Vector512<int> wideHigh) = Vector512.Widen(source);
                Vector512<float> singleLow = Vector512.ConvertToSingle(wideLow);
                Vector512<float> singleHigh = Vector512.ConvertToSingle(wideHigh);
                singleLow *= factorVec;
                singleHigh *= factorVec;
                wideLow = Vector512.ConvertToInt32(singleLow);
                wideHigh = Vector512.ConvertToInt32(singleHigh);
                Vector512<short> result = Vector512.Narrow(wideLow, wideHigh);
                result.CopyTo(span);
                span = span.Slice(Vector512<short>.Count);
            }
        }

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

        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<float> factorVec = Vector128.Create(factor);

            while (span.Length >= Vector128<short>.Count)
            {
                Vector128<short> source = Vector128.Create(span);
                (Vector128<int> wideLow, Vector128<int> wideHigh) = Vector128.Widen(source);
                Vector128<float> singleLow = Vector128.ConvertToSingle(wideLow);
                Vector128<float> singleHigh = Vector128.ConvertToSingle(wideHigh);
                singleLow *= factorVec;
                singleHigh *= factorVec;
                wideLow = Vector128.ConvertToInt32(singleLow);
                wideHigh = Vector128.ConvertToInt32(singleHigh);
                Vector128<short> result = Vector128.Narrow(wideLow, wideHigh);
                result.CopyTo(span);
                span = span.Slice(Vector128<short>.Count);
            }
        }

        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (short)(span[i] * factor);
        }
    }
}
