using System.Runtime.InteropServices;

namespace System.Security.Cryptography;

internal static class CryptographicOperationsExtensions
{
    extension(CryptographicOperations)
    {
        public static bool FixedTimeEquals(ReadOnlySpan<char> expected, ReadOnlySpan<char> actual)
        {
            ArgumentOutOfRangeException.ThrowIfZero(expected.Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(expected.Length, 1000);

            return
                expected.Length == actual.Length &&
                CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(expected), MemoryMarshal.AsBytes(actual));
        }
    }
}
