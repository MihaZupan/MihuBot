#nullable enable

namespace MihuBot.Helpers;

/// <summary>Compares byte arrays by content. Hash codes are randomized per process, like those of strings.</summary>
public sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public static ByteArrayComparer Instance { get; } = new();

    private ByteArrayComparer() { }

    public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

    public int GetHashCode(byte[] obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}
