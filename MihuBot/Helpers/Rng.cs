using System.Buffers;
using System.Numerics;
using System.Numerics.Tensors;
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
        ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(count));

        int heads = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(count / 8, 1024 * 1024));

        while (count >= 8)
        {
            Span<byte> slice = buffer.AsSpan(0, Math.Min(buffer.Length, count / 8));
            RandomNumberGenerator.Fill(slice);
            heads += (int)TensorPrimitives.PopCount(slice);
            count -= slice.Length * 8;
        }

        ArrayPool<byte>.Shared.Return(buffer);

        if (count > 0)
        {
            heads += BitOperations.PopCount((uint)RandomNumberGenerator.GetInt32(1 << count));
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
