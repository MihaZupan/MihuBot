using MihuBot.Helpers;

namespace MihuBot.Tests;

public sealed class ValueByteBuilderTests
{
    [Fact]
    public void GrowingPastTheInitialBufferKeepsTheWrittenBytes()
    {
        byte[] expected = new byte[1000];
        Random.Shared.NextBytes(expected);

        var builder = new ValueByteBuilder(stackalloc byte[4]);

        builder.Append(expected[0]);
        builder.AppendBigEndian(0x01020304);
        builder.Append(expected.AsSpan(5, 100));

        for (int i = 105; i < expected.Length; i++)
        {
            builder.Append(expected[i]);
        }

        "\x01\x02\x03\x04"u8.CopyTo(expected.AsSpan(1));

        Assert.Equal(expected.Length, builder.Length);
        Assert.Equal(expected, builder.ToArray());

        builder.Dispose();
    }
}
