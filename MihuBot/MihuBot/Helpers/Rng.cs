using System;
using System.Collections.Generic;
using System.Linq;
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

        public static T RandomExcludingSelf<T>(this T[] choices, T self) where T : class
        {
            for (int i = 0; i < choices.Length; i++)
            {
                T random = choices.Random();

                if (!ReferenceEquals(random, self))
                    return random;
            }

            T[] elementsWithoutSelf = choices.Where(e => !ReferenceEquals(e, self)).ToArray();

            if (elementsWithoutSelf.Length == 0)
                throw new ArgumentException("Choices must contain at least 1 element other than self", nameof(choices));

            return elementsWithoutSelf.Random();
        }

        public static (T First, T Second)[] GeneratePairs<T>(T[] values)
        {
            if (values.Length % 2 != 0)
                throw new ArgumentException("Must have an even number of values", nameof(values));

            T[] valuesCopy = new T[values.Length];
            Array.Copy(values, valuesCopy, values.Length);

            FisherYatesShuffle(valuesCopy);

            var pairs = new (T First, T Second)[values.Length / 2];

            for (int i = 0; i < pairs.Length; i++)
            {
                pairs[i] = (valuesCopy[i * 2], valuesCopy[i * 2 + 1]);
            }

            return pairs;
        }
        
        private static void FisherYatesShuffle<T>(T[] array)
        {
            int n = array.Length;
            while (n > 1)
            {
                int k = Next(n--);
                T temp = array[n];
                array[n] = array[k];
                array[k] = temp;
            }
        }
    }
}
