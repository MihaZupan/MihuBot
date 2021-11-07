using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Helpers
{
    public static class Rng
    {
        [ThreadStatic]
        private static ulong _rngBoolCache = 0;

        public static bool Chance(int oneInX)
        {
            return Next(oneInX) == 0;
        }

        public static int Next(int mod)
        {
            if (mod == 2)
                return Bool() ? 0 : 1;

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
            return array.Length switch
            {
                0 => throw new ArgumentException("Array is empty", nameof(array)),
                1 => array[0],
                _ => array[Next(array.Length)]
            };
        }

        public static T Random<T>(this IReadOnlyCollection<T> collection)
        {
            return collection.Count switch
            {
                0 => throw new ArgumentException("Collection is empty", nameof(collection)),
                1 => collection.First(),
                _ => collection.Skip(Next(collection.Count)).First()
            };
        }

        public static T Random<T>(this List<T> list)
        {
            if (list.Count < 2)
            {
                if (list.Count == 0)
                    throw new ArgumentException("List is empty", nameof(list));

                return list[0];
            }

            return list[Next(list.Count)];
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

        public static void Shuffle<T>(T[] array)
        {
            FisherYatesShuffle(array);
        }
    }
}
