using System.Runtime.CompilerServices;
using System.Threading;

namespace MihuBot.Helpers
{
    public static class PreNet5Compat
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong InterlockedCompareExchange(ref ulong location1, ulong value, ulong comparand) =>
            (ulong)Interlocked.CompareExchange(ref Unsafe.As<ulong, long>(ref location1), (long)value, (long)comparand);
    }
}
