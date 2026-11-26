using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Molly.Sharing;

/// <summary>
/// Bytewise Shamir secret sharing over GF(2^8) with the AES polynomial <c>x^8+x^4+x^3+x+1</c> (0x11B),
/// as specified in Sharing/README.md. Clients split, the server reconstructs.
/// </summary>
/// <remarks>
/// <para>
/// This is the standard construction (also used by SLIP-0039 and HashiCorp Vault): every byte of the secret
/// is shared independently with its own polynomial of degree M-1, whose other coefficients are uniformly
/// random bytes (zero included). Any M-1 shares are then independent of the secret, information-theoretically,
/// as long as the coefficients come from a CSPRNG and the indices are distinct and non-zero, which limits a
/// secret to 255 shares. Splitting bytewise rather than over one large field loses nothing: the polynomials
/// are independent, and each byte is perfectly hidden on its own.
/// </para>
/// <para>
/// Shamir sharing doesn't authenticate shares - a wrong share silently yields a wrong secret. Sharing/README.md
/// gets that from the SKEY envelopes around every share and from authenticating the reconstructed secret
/// against DATA_KEY_SEALED_SHAREDENC.
/// </para>
/// <para>
/// The arithmetic is constant-time: no branches on, or table lookups indexed by, secret share bytes.
/// </para>
/// </remarks>
internal static class MollyShamir
{
    public const int MaxShares = 255;

    /// <summary>Multiplication modulo 0x11B, without data-dependent branches, lookups or loops.</summary>
    public static byte Multiply(byte a, byte b) => Reduce(CarrylessMultiply(a, b));

    /// <summary>
    /// The 15-bit carry-less product of two bytes, computed with ordinary integer multiplications.
    /// </summary>
    /// <remarks>
    /// Both operands are split into four parts holding every fourth bit (two bits each), so in every partial
    /// product at most two terms land on any bit position. Their sum therefore never carries more than one bit
    /// into the next position, which belongs to another residue class mod 4 and is masked off - leaving the
    /// parity, i.e. the XOR, of the terms. Each residue class collects the four partial products that land on it.
    /// </remarks>
    private static uint CarrylessMultiply(byte a, byte b)
    {
        uint a0 = a & 0x11u, a1 = a & 0x22u, a2 = a & 0x44u, a3 = a & 0x88u;
        uint b0 = b & 0x11u, b1 = b & 0x22u, b2 = b & 0x44u, b3 = b & 0x88u;

        uint z0 = (a0 * b0) ^ (a1 * b3) ^ (a2 * b2) ^ (a3 * b1);
        uint z1 = (a0 * b1) ^ (a1 * b0) ^ (a2 * b3) ^ (a3 * b2);
        uint z2 = (a0 * b2) ^ (a1 * b1) ^ (a2 * b0) ^ (a3 * b3);
        uint z3 = (a0 * b3) ^ (a1 * b2) ^ (a2 * b1) ^ (a3 * b0);

        return (z0 & 0x1111u) | (z1 & 0x2222u) | (z2 & 0x4444u) | (z3 & 0x8888u);
    }

    /// <summary>
    /// Reduces a 15-bit product modulo 0x11B. Since <c>x^8 = x^4 + x^3 + x + 1</c>, the high bits fold back in
    /// as <c>h ^ h &lt;&lt; 1 ^ h &lt;&lt; 3 ^ h &lt;&lt; 4</c>. The first fold leaves at most 3 high bits, the second none.
    /// </summary>
    private static byte Reduce(uint product)
    {
        uint high = product >> 8;
        product = (product & 0xFF) ^ high ^ (high << 1) ^ (high << 3) ^ (high << 4);

        high = product >> 8;
        product ^= high ^ (high << 1) ^ (high << 3) ^ (high << 4);

        return (byte)product;
    }

    /// <summary>The multiplicative inverse as <c>a^254</c>, with a fixed sequence of multiplications. Zero maps to zero.</summary>
    public static byte Inverse(byte a)
    {
        byte result = 1;
        byte power = a;

        // power runs through a^2, a^4, ..., a^128, whose product is a^254.
        for (int i = 0; i < 7; i++)
        {
            power = Multiply(power, power);
            result = Multiply(result, power);
        }

        return result;
    }

    /// <summary>
    /// Reconstructs the secret with Lagrange interpolation at <c>x = 0</c>. The caller supplies exactly the
    /// shares to combine, all of the same length and with distinct non-zero indices.
    /// </summary>
    /// <exception cref="ArgumentException">The shares are empty, mismatched in length, or reuse or include index 0.</exception>
    public static byte[] Combine(IReadOnlyList<(byte Index, byte[] Value)> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);

        if (shares.Count == 0)
        {
            throw new ArgumentException("At least one share is required.", nameof(shares));
        }

        int length = shares[0].Value.Length;
        Span<bool> seen = stackalloc bool[256];

        foreach ((byte index, byte[] value) in shares)
        {
            if (index == 0 || seen[index] || value.Length != length)
            {
                throw new ArgumentException("Shares need distinct non-zero indices and equal lengths.", nameof(shares));
            }

            seen[index] = true;
        }

        byte[] secret = new byte[length];

        for (int i = 0; i < shares.Count; i++)
        {
            byte xi = shares[i].Index;
            byte numerator = 1;
            byte denominator = 1;

            for (int j = 0; j < shares.Count; j++)
            {
                if (j == i)
                {
                    continue;
                }

                // In characteristic 2, (0 - xj) / (xi - xj) is xj / (xj ^ xi).
                byte xj = shares[j].Index;
                numerator = Multiply(numerator, xj);
                denominator = Multiply(denominator, (byte)(xj ^ xi));
            }

            byte basis = Multiply(numerator, Inverse(denominator));
            byte[] value = shares[i].Value;

            for (int k = 0; k < length; k++)
            {
                secret[k] ^= Multiply(value[k], basis);
            }
        }

        return secret;
    }

    /// <summary>Splits <paramref name="secret"/> into <paramref name="count"/> shares, any <paramref name="threshold"/> of which reconstruct it.</summary>
    public static (byte Index, byte[] Value)[] Split(ReadOnlySpan<byte> secret, int threshold, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, threshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxShares);

        byte[][] coefficients = new byte[threshold - 1][];

        for (int i = 0; i < coefficients.Length; i++)
        {
            coefficients[i] = RandomNumberGenerator.GetBytes(secret.Length);
        }

        var shares = new (byte Index, byte[] Value)[count];

        for (int x = 1; x <= count; x++)
        {
            shares[x - 1] = ((byte)x, Evaluate(secret, coefficients, (byte)x));
        }

        foreach (byte[] coefficient in coefficients)
        {
            CryptographicOperations.ZeroMemory(coefficient);
        }

        return shares;
    }

    /// <summary>Evaluates <c>f(x) = s + a1 x + ... + a(M-1) x^(M-1)</c> bytewise with Horner's rule.</summary>
    public static byte[] Evaluate(ReadOnlySpan<byte> secret, IReadOnlyList<byte[]> coefficients, byte x)
    {
        byte[] result = new byte[secret.Length];

        for (int k = 0; k < secret.Length; k++)
        {
            byte y = 0;

            for (int c = coefficients.Count - 1; c >= 0; c--)
            {
                y = (byte)(Multiply(y, x) ^ coefficients[c][k]);
            }

            result[k] = (byte)(Multiply(y, x) ^ secret[k]);
        }

        return result;
    }
}
