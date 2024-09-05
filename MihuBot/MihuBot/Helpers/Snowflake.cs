using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;

namespace MihuBot.Helpers;

// Same format as Discord's Snowflakes
public static class Snowflake
{
    private const int MaxCounterValue = (1 << 17) - 1;

    public static ulong Next()
    {
        while (true)
        {
            if (State.TryGetNext(out ulong result))
            {
                return result;
            }

            Thread.Sleep(1);
        }
    }

    public static string NextString()
    {
        ulong snowflake = Next();

        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, snowflake);

        return Base64Url.EncodeToString(buffer);
    }

    public static DateTimeOffset FromSnowflake(ReadOnlySpan<char> snowflakeString)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        OperationStatus status = Base64Url.DecodeFromChars(snowflakeString, buffer, out int charsConsumed, out int bytesWritten);

        if (status != OperationStatus.Done || charsConsumed != snowflakeString.Length || bytesWritten != sizeof(ulong))
        {
            throw new ArgumentException("Invalid snowflake string", nameof(snowflakeString));
        }

        ulong snowflake = BinaryPrimitives.ReadUInt64BigEndian(buffer);
        return SnowflakeUtils.FromSnowflake(snowflake);
    }

    private sealed class State
    {
        private static volatile State s_last = new(0);

        private readonly long _ms;
        private ulong _counter;

        private State(long ms) => _ms = ms;

        public static bool TryGetNext(out ulong result)
        {
            long time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            State state = s_last;

            if (state._ms < time)
            {
                Interlocked.CompareExchange(ref s_last, new State(time), state);
                state = s_last;
            }

            ulong count = Interlocked.Increment(ref state._counter);
            if (count <= MaxCounterValue)
            {
                result = SnowflakeUtils.ToSnowflake(DateTimeOffset.FromUnixTimeMilliseconds(state._ms)) | count;
                return true;
            }

            result = 0;
            return false;
        }
    }
}
