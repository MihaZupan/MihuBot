#nullable enable

namespace MihuBot.Molly.Attestation;

public enum MollyAttestationMode
{
    /// <summary>
    /// A persistent app attest key (<c>PURPOSE_ATTEST_KEY</c>) is pinned, and every fresh login key has
    /// to be attested by it, which cryptographically ties each login to the enrolled hardware.
    /// </summary>
    Strict = 1,

    /// <summary>
    /// For devices without app attest key support: a pinned hardware signing key signs a transcript that
    /// includes fresh manufacturer-rooted attestation. Weaker, as the fresh evidence isn't bound to the
    /// enrolled hardware by the attestation itself.
    /// </summary>
    Fallback = 2,
}

public static class MollyAttestationModeExtensions
{
    public static string ToWireValue(this MollyAttestationMode mode) => mode switch
    {
        MollyAttestationMode.Strict => "strict",
        MollyAttestationMode.Fallback => "fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool TryParse(string? value, out MollyAttestationMode mode)
    {
        mode = value switch
        {
            "strict" => MollyAttestationMode.Strict,
            "fallback" => MollyAttestationMode.Fallback,
            _ => default,
        };

        return mode != default;
    }
}

/// <summary>
/// What an entry pinned at enrollment, plus high-water marks that only ever move forward.
/// Stored as JSON with per-entry authenticated encryption.
/// </summary>
public sealed record MollyAttestationState
{
    public const int CurrentVersion = 1;

    public required int Version { get; init; }

    public required MollyAttestationMode Mode { get; init; }

    /// <summary>DER SubjectPublicKeyInfo of the persistent hardware signing key.</summary>
    public required byte[] SigningPublicKey { get; init; }

    /// <summary>DER SubjectPublicKeyInfo of the persistent attest key. Only set in <see cref="MollyAttestationMode.Strict"/>.</summary>
    public byte[]? AttestPublicKey { get; init; }

    /// <summary>The security level of every key: StrongBox or TEE.</summary>
    public required AndroidSecurityLevel SecurityLevel { get; init; }

    public required byte[] VerifiedBootKey { get; init; }

    public required AndroidVerifiedBootState VerifiedBootState { get; init; }

    public required string PackageName { get; init; }

    /// <summary>SHA-256 of the app's signing certificate. Apps signed by several certificates aren't supported.</summary>
    public required byte[] SignatureDigest { get; init; }

    public required long AppVersion { get; init; }

    public required long OsVersion { get; init; }

    /// <summary>Patch levels normalized to <c>YYYYMMDD</c>.</summary>
    public required long OsPatchLevel { get; init; }

    public long? VendorPatchLevel { get; init; }

    public long? BootPatchLevel { get; init; }

    /// <summary>
    /// Revocation-list serials of the manufacturer certificates the pinned keys were enrolled through.
    /// Checked on every login so a revocation after enrollment still takes effect.
    /// </summary>
    public required string[] PinnedCertificateSerials { get; init; }

    public required DateTime EnrolledAt { get; init; }

    public required DateTime LastAttestedAt { get; init; }
}
