using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Helpers.Crypto;

internal static class CryptographicOperationsExtensions
{
    extension(CryptographicOperations)
    {
        public static bool FixedTimeEquals(ReadOnlySpan<char> expected, ReadOnlySpan<char> actual)
        {
            ArgumentOutOfRangeException.ThrowIfZero(expected.Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(expected.Length, 4096);

            return CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(expected), MemoryMarshal.AsBytes(actual));
        }
    }
}
