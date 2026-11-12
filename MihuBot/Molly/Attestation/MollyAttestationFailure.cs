#nullable enable

namespace MihuBot.Molly.Attestation;

/// <summary>
/// Why an attested login was rejected. Only logged and used to decide whether to lock the entry:
/// clients are always told <c>invalid</c>, so they don't learn why.
/// </summary>
public enum MollyAttestationFailure
{
    None,

    // The request isn't an attested login at all.
    MissingProof,
    MalformedProof,

    // The login may succeed in a new exchange, e.g. after a transport-level retry re-sealed a signed request.
    TransportKeyTooOld,
    ChallengeMismatch,
    InvalidSignature,
    ConcurrentLogin,

    // The server can't verify anything right now.
    RevocationDataUnavailable,
    RevocationDataStale,

    // The evidence is rejected for good.
    UnreadablePinnedState,
    UnsupportedStateVersion,
    ModeMismatch,
    MalformedCertificate,
    MissingSigningKeyChain,
    UnexpectedAttestKeyChain,
    EnrollmentEvidenceMismatch,
    MissingPinnedAttestKey,
    PinnedCertificateRevoked,
    ChainTooShort,
    UntrustedRoot,
    InvalidChain,
    MissingAttestationExtension,
    AttestationExtensionOutsideLeaf,
    CertificateRevoked,
    CertificateNotValidNow,
    NotAttestedByAttestKey,
    AttestKeyNotHardwareBacked,
    AttestKeyUnexpectedProperties,
    SigningKeyNotHardwareBacked,
    SigningKeyUnexpectedProperties,
    AttestationVersionTooOld,
    NotHardwareBacked,
    KeyNotGeneratedOnDevice,
    MissingRootOfTrust,
    BootloaderUnlocked,
    MissingOsVersion,
    MalformedPatchLevel,
    UnexpectedApplication,
    PersistentKeyBootMismatch,
    PersistentKeyApplicationMismatch,
    SecurityLevelMismatch,
    KeysNotDistinct,
    SecurityLevelChanged,
    VerifiedBootChanged,
    ApplicationChanged,
    VersionRollback,
}

public static class MollyAttestationFailureExtensions
{
    /// <summary>
    /// Whether the evidence itself was rejected, as opposed to a request that wasn't attested, one that may
    /// succeed in a new exchange, or a server that can't verify right now. Locks an enrolled entry.
    /// </summary>
    public static bool IsPermanent(this MollyAttestationFailure failure) => failure is not (
        MollyAttestationFailure.None or
        MollyAttestationFailure.MissingProof or
        MollyAttestationFailure.MalformedProof or
        MollyAttestationFailure.TransportKeyTooOld or
        MollyAttestationFailure.ChallengeMismatch or
        MollyAttestationFailure.InvalidSignature or
        MollyAttestationFailure.ConcurrentLogin or
        MollyAttestationFailure.RevocationDataUnavailable or
        MollyAttestationFailure.RevocationDataStale or
        // Clock skew or a not yet refreshed short-lived (RKP) certificate, rather than tampering.
        MollyAttestationFailure.CertificateNotValidNow);
}
