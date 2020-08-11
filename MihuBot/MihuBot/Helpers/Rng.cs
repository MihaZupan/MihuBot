using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Helpers
{
    public static class Rng
    {
        public static bool Chance(int oneInX)
        {
            return Next(oneInX) == 0;
        }

        public static int Next(int mod)
        {
            if (mod <= 0)
                throw new ArgumentOutOfRangeException(nameof(mod), "Must be > 0");

            Span<byte> buffer = stackalloc byte[mod <= 1000 ? 4 : 16];
            RandomNumberGenerator.Fill(buffer);
            var number = new BigInteger(buffer, isUnsigned: true);

            return (int)(number % mod);
        }

        public static bool Bool()
        {
            Span<byte> buffer = stackalloc byte[1];
            RandomNumberGenerator.Fill(buffer);
            return (buffer[0] & 1) == 0;
        }

        public static int FlipCoins(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Must be > 0");

            const int StackallocSize = 4096;
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

            RandomNumberGenerator.Fill(memory.Slice(0, count));
            foreach (byte b in memory.Slice(0, count))
                heads += b & 1;

            return heads;
        }

        public static T Random<T>(this T[] array)
        {
            return array[Next(array.Length)];
        }
    }
}
