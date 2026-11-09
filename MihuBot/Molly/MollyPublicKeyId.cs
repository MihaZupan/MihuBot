using System.Runtime.CompilerServices;

#nullable enable

namespace MihuBot.Molly;

/// <summary>The first 16 bytes of SHA-512 of a transport public key, stored inline.</summary>
public readonly struct MollyPublicKeyId : IEquatable<MollyPublicKeyId>
{
    private readonly KeyIdBuffer _value;

    [InlineArray(MollyRequestProtector.RecipientKeyIdLength)]
    private struct KeyIdBuffer { private byte _b; }

    public MollyPublicKeyId(ReadOnlySpan<byte> keyId)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(keyId.Length, MollyRequestProtector.RecipientKeyIdLength);
        keyId.CopyTo(_value);
    }

    public bool Equals(MollyPublicKeyId other) => ((ReadOnlySpan<byte>)_value).SequenceEqual(other._value);

    public override bool Equals(object? obj) => obj is MollyPublicKeyId other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.AddBytes(_value);
        return hash.ToHashCode();
    }

    public static bool operator ==(MollyPublicKeyId left, MollyPublicKeyId right) => left.Equals(right);

    public static bool operator !=(MollyPublicKeyId left, MollyPublicKeyId right) => !left.Equals(right);
}
