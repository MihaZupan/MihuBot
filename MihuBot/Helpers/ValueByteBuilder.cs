using System.Buffers;
using System.Buffers.Binary;

#nullable enable

namespace MihuBot.Helpers;

/// <summary>
/// Builds a sequence of bytes in a caller-provided (usually stack) buffer, moving to pooled arrays once it outgrows it.
/// Like <c>ValueStringBuilder</c>, it's a mutable struct: pass it by <see langword="ref"/>, and call <see cref="Dispose"/> when done.
/// </summary>
public ref struct ValueByteBuilder
{
    private byte[]? _arrayToReturnToPool;
    private Span<byte> _bytes;
    private int _pos;

    public ValueByteBuilder(Span<byte> initialBuffer)
    {
        _bytes = initialBuffer;
    }

    public ValueByteBuilder(int initialCapacity)
    {
        _arrayToReturnToPool = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _bytes = _arrayToReturnToPool;
    }

    public readonly int Length => _pos;

    public readonly ReadOnlySpan<byte> AsSpan() => _bytes.Slice(0, _pos);

    public readonly byte[] ToArray() => AsSpan().ToArray();

    public void Append(byte value)
    {
        if ((uint)_pos >= (uint)_bytes.Length)
        {
            Grow(1);
        }

        _bytes[_pos++] = value;
    }

    public void Append(scoped ReadOnlySpan<byte> value)
    {
        value.CopyTo(AppendSpan(value.Length));
    }

    public void AppendBigEndian(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(AppendSpan(sizeof(uint)), value);
    }

    public void AppendBigEndian(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(AppendSpan(sizeof(long)), value);
    }

    /// <summary>Reserves <paramref name="length"/> bytes at the end and returns them for the caller to fill in.</summary>
    public Span<byte> AppendSpan(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (_bytes.Length - _pos < length)
        {
            Grow(length);
        }

        Span<byte> span = _bytes.Slice(_pos, length);
        _pos += length;
        return span;
    }

    public void Dispose()
    {
        byte[]? toReturn = _arrayToReturnToPool;
        this = default;

        if (toReturn is not null)
        {
            ArrayPool<byte>.Shared.Return(toReturn);
        }
    }

    private void Grow(int additionalCapacityBeyondPos)
    {
        int required = checked(_pos + additionalCapacityBeyondPos);
        int newCapacity = (int)Math.Max((uint)required, Math.Min((uint)_bytes.Length * 2, (uint)Array.MaxLength));

        byte[] poolArray = ArrayPool<byte>.Shared.Rent(newCapacity);
        _bytes.Slice(0, _pos).CopyTo(poolArray);

        byte[]? toReturn = _arrayToReturnToPool;
        _bytes = _arrayToReturnToPool = poolArray;

        if (toReturn is not null)
        {
            ArrayPool<byte>.Shared.Return(toReturn);
        }
    }
}
