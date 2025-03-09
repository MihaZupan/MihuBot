using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Helpers;

public static class Rng
{
    public static bool Bool() =>
        Chance(2);

    public static bool Chance(int oneInX) =>
        Next(oneInX) == 0;

    public static int Next(int toExclusive) =>
        RandomNumberGenerator.GetInt32(toExclusive);

    public static int Next(int fromInclusive, int toExclusive) =>
        RandomNumberGenerator.GetInt32(fromInclusive, toExclusive);

    public static int FlipCoins(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Must be > 0");

        const int StackallocSize = 1024;
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

        return array[Next(array.Length)];
    }

    public static T Random<T>(this IReadOnlyCollection<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        int count = collection.Count;
        ArgumentOutOfRangeException.ThrowIfZero(count);

        return count == 0 ? collection.First() : collection.Skip(Next(count)).First();
    }

    public static T Random<T>(this List<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentOutOfRangeException.ThrowIfZero(list.Count);

        return list[Next(list.Count)];
    }
}
