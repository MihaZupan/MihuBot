using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Helpers;

public static class Rng
{
    [ThreadStatic]
    private static ulong _rngBoolCache;

    public static bool Chance(int oneInX)
    {
        return Next(oneInX) == 0;
    }

    public static int Next(int minInclusive, int maxExclusive)
    {
        return minInclusive + Next(maxExclusive - minInclusive);
    }

    public static int Next(int mod)
    {
        if (mod == 2)
            return Bool() ? 0 : 1;

        if (mod == 1)
            return 0;

        if (mod <= 0)
            throw new ArgumentOutOfRangeException(nameof(mod), "Must be > 0");

        Span<byte> buffer = stackalloc byte[mod <= 1000 ? 4 : 8];
        RandomNumberGenerator.Fill(buffer);
        var number = new BigInteger(buffer, isUnsigned: true);

        return (int)(number % mod);
    }

    public static bool Bool()
    {
        const ulong LengthOne = 1ul << 56;
        const ulong LengthMask = 0xFFul << 56;
        const ulong RngBitsMask = ~LengthMask;

        ulong value = _rngBoolCache;
        if (value != 0)
        {
            _rngBoolCache = ((value & LengthMask) - LengthOne) | ((value & RngBitsMask) >> 1);
            return (value & 1) == 1;
        }

        ulong buffer = 0;
        RandomNumberGenerator.Fill(MemoryMarshal.CreateSpan(ref Unsafe.As<ulong, byte>(ref buffer), 7));

        if (!BitConverter.IsLittleEndian)
            buffer >>= 8;

        _rngBoolCache = (55 * LengthOne) | (buffer >> 1);
        return (buffer & 1) == 1;
    }

    public static int FlipCoins(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Must be > 0");

        const int StackallocSize = 128;
        const int SizeAsUlong = StackallocSize / 8;

        int heads = 0;

        Span<byte> memory = stackalloc byte[StackallocSize];

        while (count >= 64)
        {
            RandomNumberGenerator.Fill(memory);

            Span<ulong> memoryAsLongs = MemoryMarshal.CreateSpan(
                ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(memory)),
                Math.Min(SizeAsUlong, count >> 6));

            foreach (ulong e in memoryAsLongs)
                heads += BitOperations.PopCount(e);

            count -= memoryAsLongs.Length << 6;
        }

        if (count > 0)
        {
            RandomNumberGenerator.Fill(memory.Slice(0, (count + 7) >> 3));

            foreach (byte b in memory.Slice(0, count >> 3))
                heads += BitOperations.PopCount(b);

            if ((count & 7) != 0)
            {
                uint lastBits = memory[count >> 3] & ((1u << (count & 7)) - 1);
                heads += BitOperations.PopCount(lastBits);
            }
        }

        return heads;
    }

    public static T Random<T>(this T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfZero(array.Length);

        return array.Length == 1 ? array[0] : array[Next(array.Length)];
    }

    public static T Random<T>(this IReadOnlyCollection<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        int count = collection.Count;
        ArgumentOutOfRangeException.ThrowIfZero(count);

        return count == 0 ? collection.First() : collection.Skip(Next(collection.Count)).First();
    }

    public static T Random<T>(this List<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentOutOfRangeException.ThrowIfZero(list.Count);

        return list.Count == 1 ? list[0] : list[Next(list.Count)];
    }
}
