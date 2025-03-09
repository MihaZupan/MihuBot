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
        return GetString(Next());
    }

    public static string GetString(ulong snowflake)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, snowflake);

        return Base64Url.EncodeToString(buffer.TrimEnd((byte)0));
    }

    public static bool TryGetFromString(ReadOnlySpan<char> snowflakeString, out ulong snowflake)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        buffer.Clear();

        OperationStatus status = Base64Url.DecodeFromChars(snowflakeString, buffer, out int charsConsumed, out int bytesWritten);

        if (status != OperationStatus.Done || charsConsumed != snowflakeString.Length || bytesWritten < 5)
        {
            snowflake = 0;
            return false;
        }

        snowflake = BinaryPrimitives.ReadUInt64BigEndian(buffer);
        return true;
    }

    public static ulong FromString(ReadOnlySpan<char> snowflakeString)
    {
        if (!TryGetFromString(snowflakeString, out ulong snowflake))
        {
            throw new ArgumentException("Invalid snowflake string", nameof(snowflakeString));
        }

        return snowflake;
    }

    public static DateTimeOffset FromSnowflake(ReadOnlySpan<char> snowflakeString)
    {
        return SnowflakeUtils.FromSnowflake(FromString(snowflakeString));
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

            ulong count = Interlocked.Increment(ref state._counter) - 1;
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
