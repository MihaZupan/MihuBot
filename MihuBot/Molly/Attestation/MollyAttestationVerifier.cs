using System.Buffers;
using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Molly.Attestation;

/// <summary>An app build allowed to enroll: its package name and the SHA-256 digest of a signing certificate.</summary>
public sealed record MollyAttestationApp(string PackageName, byte[] SignatureDigest);

/// <summary>The decoded attestation part of a login request.</summary>
/// <param name="FreshChain">The per-login key's chain, attested with the exchange's challenge (<see cref="MollyAttestationTranscript.Challenge"/>).</param>
/// <param name="SigningKeyChain">The persistent signing key's chain. Required to enroll, and only checked against the pins afterwards.</param>
/// <param name="AttestKeyChain">The persistent attest key's manufacturer chain, in strict mode. As for <paramref name="SigningKeyChain"/>.</param>
/// <param name="Signature">DER ECDSA P-256/SHA-256 signature by the persistent signing key over the transcript.</param>
public sealed record MollyAttestationEvidence(MollyAttestationMode Mode, byte[][] FreshChain, byte[][] SigningKeyChain, byte[][] AttestKeyChain, byte[] Signature);

/// <summary>The state to pin on success, or why the evidence was rejected.</summary>
public sealed record MollyAttestationResult(MollyAttestationState? State, MollyAttestationFailure Failure)
{
    public static MollyAttestationResult Success(MollyAttestationState state) => new(state, MollyAttestationFailure.None);

    public static MollyAttestationResult Fail(MollyAttestationFailure failure) => new(null, failure);
}

/// <summary>
/// Verifies Android hardware key attestation evidence for Molly logins. Has no I/O, so the whole policy
/// can be exercised with deterministic fixture chains.
/// </summary>
/// <remarks>
/// <para>
/// Enrollment pins app-generated persistent hardware keys plus the device's verified boot identity.
/// Each login must then present a freshly generated hardware key attested with a challenge derived from
/// a recent transport exchange, and a signature by the pinned signing key over a transcript binding the
/// credential, the evidence and that exchange.
/// </para>
/// <para>
/// In <see cref="MollyAttestationMode.Strict"/> mode the fresh key must be attested by the pinned attest
/// key, so the evidence provably comes from the enrolled hardware. Historical manufacturer certificates
/// aren't re-validated for expiry then (RKP certificates are short-lived), only for revocation.
/// <see cref="MollyAttestationMode.Fallback"/> relies on a fresh manufacturer-rooted chain instead.
/// </para>
/// </remarks>
public sealed class MollyAttestationVerifier
{
    /// <summary>Keymaster 4 (Android 9). Older versions lack fields this policy relies on.</summary>
    public const int MinimumAttestationVersion = 3;

    public const int MaxChainLength = 8;

    public const int MaxSignatureLength = 80;

    /// <summary>Device clocks and certificate issuance times don't have to agree exactly with the server.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private static readonly SearchValues<char> s_packageNameChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    private readonly MollyAttestationTrustAnchors _anchors;
    private readonly MollyAttestationApp[] _apps;
    private readonly TimeProvider _time;

    public MollyAttestationVerifier(MollyAttestationTrustAnchors anchors, IEnumerable<MollyAttestationApp> apps, TimeProvider? time = null)
    {
        _anchors = anchors;
        _apps = [.. apps];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Verifies a first attestation for an entry and returns the state to pin.
    /// The caller is responsible for checking the age of the exchange's transport key.
    /// </summary>
    public MollyAttestationResult VerifyEnrollment(MollyAttestationEvidence evidence, ReadOnlySpan<byte> keyHash, MollyExchangeContext exchange, MollyRevocationSnapshot revocations)
    {
        if (!TryParseChain(evidence.FreshChain, out var fresh) ||
            !TryParseChain(evidence.SigningKeyChain, out var signing) ||
            !TryParseChain(evidence.AttestKeyChain, out var attest))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.MalformedCertificate);
        }

        if (signing.Length == 0)
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.MissingSigningKeyChain);
        }

        DateTimeOffset now = _time.GetUtcNow();
        MollyAttestationCertificate signingLeaf = signing[0];
        MollyAttestationCertificate? attestLeaf = null;
        string[] pinnedSerials;
        MollyAttestationFailure failure;

        if (evidence.Mode == MollyAttestationMode.Strict)
        {
            if ((failure = VerifyManufacturerChain(attest, revocations, now)) != MollyAttestationFailure.None)
            {
                return MollyAttestationResult.Fail(failure);
            }

            attestLeaf = attest[0];

            if ((failure = CheckAttestKey(attestLeaf.KeyDescription!)) != MollyAttestationFailure.None ||
                (failure = VerifyAttestedByPinnedKey(signing, attestLeaf.SubjectPublicKeyInfo)) != MollyAttestationFailure.None ||
                (failure = VerifyAttestedByPinnedKey(fresh, attestLeaf.SubjectPublicKeyInfo)) != MollyAttestationFailure.None)
            {
                return MollyAttestationResult.Fail(failure);
            }

            pinnedSerials = GetPinnedSerials(attest);
        }
        else
        {
            if (attest.Length != 0)
            {
                return MollyAttestationResult.Fail(MollyAttestationFailure.UnexpectedAttestKeyChain);
            }

            if ((failure = VerifyManufacturerChain(signing, revocations, now)) != MollyAttestationFailure.None ||
                (failure = VerifyManufacturerChain(fresh, revocations, now)) != MollyAttestationFailure.None)
            {
                return MollyAttestationResult.Fail(failure);
            }

            pinnedSerials = GetPinnedSerials(signing);
        }

        AndroidKeyDescription signingDescription = signingLeaf.KeyDescription!;

        if ((failure = CheckSigningKey(signingDescription)) != MollyAttestationFailure.None ||
            (failure = CheckFreshKey(fresh[0], MollyAttestationTranscript.Challenge(exchange), out DeviceFacts? facts)) != MollyAttestationFailure.None)
        {
            return MollyAttestationResult.Fail(failure);
        }

        // The persistent keys must belong to the same app on the same verified boot identity.
        if ((failure = CheckSameDevice(signingDescription, facts!)) != MollyAttestationFailure.None ||
            (attestLeaf is not null && (failure = CheckSameDevice(attestLeaf.KeyDescription!, facts!)) != MollyAttestationFailure.None))
        {
            return MollyAttestationResult.Fail(failure);
        }

        // All keys live in the same KeyMint instance: StrongBox or TEE.
        if (signingDescription.AttestationSecurityLevel != facts!.SecurityLevel ||
            (attestLeaf is not null && attestLeaf.KeyDescription!.AttestationSecurityLevel != facts.SecurityLevel))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.SecurityLevelMismatch);
        }

        if (fresh[0].SubjectPublicKeyInfo.AsSpan().SequenceEqual(signingLeaf.SubjectPublicKeyInfo) ||
            (attestLeaf is not null && (attestLeaf.SubjectPublicKeyInfo.AsSpan().SequenceEqual(signingLeaf.SubjectPublicKeyInfo) || attestLeaf.SubjectPublicKeyInfo.AsSpan().SequenceEqual(fresh[0].SubjectPublicKeyInfo))))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.KeysNotDistinct);
        }

        if (!VerifyTranscript(evidence, keyHash, exchange, signingLeaf.SubjectPublicKeyInfo, attestLeaf?.SubjectPublicKeyInfo))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.InvalidSignature);
        }

        DateTime timestamp = now.UtcDateTime;

        return MollyAttestationResult.Success(new MollyAttestationState
        {
            Version = MollyAttestationState.CurrentVersion,
            Mode = evidence.Mode,
            SigningPublicKey = signingLeaf.SubjectPublicKeyInfo,
            AttestPublicKey = attestLeaf?.SubjectPublicKeyInfo,
            SecurityLevel = facts!.SecurityLevel,
            VerifiedBootKey = facts.RootOfTrust.VerifiedBootKey,
            VerifiedBootState = facts.RootOfTrust.VerifiedBootState,
            PackageName = facts.PackageName,
            SignatureDigest = facts.SignatureDigest,
            AppVersion = facts.AppVersion,
            OsVersion = facts.OsVersion,
            OsPatchLevel = facts.OsPatchLevel,
            VendorPatchLevel = facts.VendorPatchLevel,
            BootPatchLevel = facts.BootPatchLevel,
            PinnedCertificateSerials = pinnedSerials,
            EnrolledAt = timestamp,
            LastAttestedAt = timestamp,
        });
    }

    /// <summary>
    /// Verifies a login against previously pinned state and returns the updated state (new high-water marks).
    /// The caller is responsible for checking the age of the exchange's transport key.
    /// </summary>
    public MollyAttestationResult VerifyLogin(MollyAttestationEvidence evidence, ReadOnlySpan<byte> keyHash, MollyExchangeContext exchange, MollyAttestationState pinned, MollyRevocationSnapshot revocations)
    {
        if (pinned.Version != MollyAttestationState.CurrentVersion)
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.UnsupportedStateVersion);
        }

        if (evidence.Mode != pinned.Mode)
        {
            // Never silently downgrade a strict enrollment (nor change a fallback one).
            return MollyAttestationResult.Fail(MollyAttestationFailure.ModeMismatch);
        }

        // A client whose enrollment response was lost can't know it enrolled, so it may send its
        // enrollment chains again. They're only accepted for the already pinned keys, and aren't used.
        if (!TryParseChain(evidence.FreshChain, out var fresh) ||
            !TryParseChain(evidence.SigningKeyChain, out var resentSigning) ||
            !TryParseChain(evidence.AttestKeyChain, out var resentAttest))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.MalformedCertificate);
        }

        if ((resentSigning.Length != 0 && !resentSigning[0].SubjectPublicKeyInfo.AsSpan().SequenceEqual(pinned.SigningPublicKey)) ||
            (resentAttest.Length != 0 && (pinned.AttestPublicKey is null || !resentAttest[0].SubjectPublicKeyInfo.AsSpan().SequenceEqual(pinned.AttestPublicKey))))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.EnrollmentEvidenceMismatch);
        }

        foreach (string serial in pinned.PinnedCertificateSerials)
        {
            if (revocations.IsRevoked(serial))
            {
                return MollyAttestationResult.Fail(MollyAttestationFailure.PinnedCertificateRevoked);
            }
        }

        DateTimeOffset now = _time.GetUtcNow();
        MollyAttestationFailure failure;

        if (pinned.Mode == MollyAttestationMode.Strict)
        {
            if (pinned.AttestPublicKey is null)
            {
                return MollyAttestationResult.Fail(MollyAttestationFailure.MissingPinnedAttestKey);
            }

            failure = VerifyAttestedByPinnedKey(fresh, pinned.AttestPublicKey);
        }
        else
        {
            failure = VerifyManufacturerChain(fresh, revocations, now);
        }

        if (failure != MollyAttestationFailure.None ||
            (failure = CheckFreshKey(fresh[0], MollyAttestationTranscript.Challenge(exchange), out DeviceFacts? facts)) != MollyAttestationFailure.None ||
            (failure = CheckAgainstPinned(facts!, pinned)) != MollyAttestationFailure.None)
        {
            return MollyAttestationResult.Fail(failure);
        }

        if (fresh[0].SubjectPublicKeyInfo.AsSpan().SequenceEqual(pinned.SigningPublicKey) ||
            (pinned.AttestPublicKey is not null && fresh[0].SubjectPublicKeyInfo.AsSpan().SequenceEqual(pinned.AttestPublicKey)))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.KeysNotDistinct);
        }

        if (!VerifyTranscript(evidence, keyHash, exchange, pinned.SigningPublicKey, pinned.AttestPublicKey))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.InvalidSignature);
        }

        return MollyAttestationResult.Success(pinned with
        {
            AppVersion = facts!.AppVersion,
            OsVersion = facts.OsVersion,
            OsPatchLevel = facts.OsPatchLevel,
            VendorPatchLevel = facts.VendorPatchLevel ?? pinned.VendorPatchLevel,
            BootPatchLevel = facts.BootPatchLevel ?? pinned.BootPatchLevel,
            LastAttestedAt = now.UtcDateTime,
        });
    }

    private static bool TryParseChain(byte[][] encoded, out MollyAttestationCertificate[] chain)
    {
        chain = [];

        if (encoded.Length > MaxChainLength)
        {
            return false;
        }

        var parsed = new MollyAttestationCertificate[encoded.Length];

        for (int i = 0; i < encoded.Length; i++)
        {
            if (!MollyAttestationCertificate.TryParse(encoded[i], out MollyAttestationCertificate? certificate))
            {
                return false;
            }

            parsed[i] = certificate;
        }

        chain = parsed;
        return true;
    }

    /// <summary>
    /// A leaf-first chain ending in a trusted root key. Only the leaf may carry the attestation extension:
    /// a hardware key with the signing purpose can sign arbitrary certificates, so a certificate it issued
    /// below itself must never be treated as hardware evidence.
    /// </summary>
    /// <remarks>
    /// Signatures are checked from the root down, so each one uses an already authenticated issuer key:
    /// a client can't make the server do expensive work with keys of its own choosing. The root is trusted
    /// by its key alone, re-issued root certificates don't matter.
    /// </remarks>
    private MollyAttestationFailure VerifyManufacturerChain(MollyAttestationCertificate[] chain, MollyRevocationSnapshot revocations, DateTimeOffset now)
    {
        if (chain.Length < 2)
        {
            return MollyAttestationFailure.ChainTooShort;
        }

        if (!_anchors.IsTrusted(chain[^1].SubjectPublicKeyInfo))
        {
            return MollyAttestationFailure.UntrustedRoot;
        }

        // A repeated root would otherwise pose as the intermediate below it, which decides the chain's class.
        if (chain[..^1].Any(c => _anchors.IsTrusted(c.SubjectPublicKeyInfo)))
        {
            return MollyAttestationFailure.InvalidChain;
        }

        if (chain[0].KeyDescription is null)
        {
            return MollyAttestationFailure.MissingAttestationExtension;
        }

        // Expired factory-provisioned certificates stay trustworthy unless revoked, per Google. RKP
        // certificates are deliberately short-lived, so their expiry is always enforced. Classified like
        // Google's own verifier: a factory chain's intermediate below the root names a serial number.
        bool enforceIssuerExpiry = !_anchors.IsFactoryRoot(chain[^1].SubjectPublicKeyInfo) || !chain[^2].HasSerialNumberInSubject || chain.Any(c => c.HasProvisioningInfo);

        for (int i = chain.Length - 2; i >= 0; i--)
        {
            MollyAttestationCertificate certificate = chain[i];

            if (!certificate.IsIssuedBy(chain[i + 1]) || !certificate.IsSignedBy(chain[i + 1].SubjectPublicKeyInfo))
            {
                return MollyAttestationFailure.InvalidChain;
            }

            if (i > 0 && certificate.HasKeyDescriptionExtension)
            {
                return MollyAttestationFailure.AttestationExtensionOutsideLeaf;
            }

            // The leaf's serial is chosen on the device, not by an issuing authority, so only issuers are listed.
            if (i > 0 && revocations.IsRevoked(certificate.RevocationSerial))
            {
                return MollyAttestationFailure.CertificateRevoked;
            }

            // The leaf's validity period is set on the device: meaningless (it's often clamped to an expired
            // factory batch certificate) and subject to clock skew. Freshness comes from the challenge.
            if (i > 0 && !certificate.IsValidAt(now, ClockSkew, enforceIssuerExpiry))
            {
                return MollyAttestationFailure.CertificateNotValidNow;
            }
        }

        return MollyAttestationFailure.None;
    }

    /// <summary>A single certificate issued by a pinned attest key, as produced for keys generated with <c>setAttestKeyAlias</c>.</summary>
    private static MollyAttestationFailure VerifyAttestedByPinnedKey(MollyAttestationCertificate[] chain, byte[] attestPublicKey)
    {
        if (chain.Length != 1)
        {
            return MollyAttestationFailure.NotAttestedByAttestKey;
        }

        MollyAttestationCertificate leaf = chain[0];

        if (leaf.KeyDescription is null)
        {
            return MollyAttestationFailure.MissingAttestationExtension;
        }

        // As for manufacturer chains, the validity period of a device-generated certificate is meaningless.
        if (!leaf.IsSignedBy(attestPublicKey))
        {
            return MollyAttestationFailure.NotAttestedByAttestKey;
        }

        return MollyAttestationFailure.None;
    }

    private static MollyAttestationFailure CheckAttestKey(AndroidKeyDescription description)
    {
        AndroidAuthorizationList hardware = description.HardwareEnforced;

        if (!IsHardware(description))
        {
            return MollyAttestationFailure.AttestKeyNotHardwareBacked;
        }

        // Keeps every certificate an attest key issues ECDSA. KeyMint would also allow Ed25519 attest keys.
        if (hardware.Purposes is not [AndroidKeyDescription.PurposeAttestKey] || hardware.Origin != AndroidKeyDescription.OriginGenerated || !IsEcP256(hardware))
        {
            return MollyAttestationFailure.AttestKeyUnexpectedProperties;
        }

        return MollyAttestationFailure.None;
    }

    private static MollyAttestationFailure CheckSigningKey(AndroidKeyDescription description)
    {
        AndroidAuthorizationList hardware = description.HardwareEnforced;

        if (!IsHardware(description))
        {
            return MollyAttestationFailure.SigningKeyNotHardwareBacked;
        }

        if (hardware.Purposes is not { } purposes ||
            !purposes.Contains(AndroidKeyDescription.PurposeSign) ||
            purposes.AsSpan().ContainsAnyExcept(AndroidKeyDescription.PurposeSign, AndroidKeyDescription.PurposeVerify) ||
            hardware.Origin != AndroidKeyDescription.OriginGenerated ||
            !IsEcP256(hardware))
        {
            return MollyAttestationFailure.SigningKeyUnexpectedProperties;
        }

        return MollyAttestationFailure.None;
    }

    private static bool IsEcP256(AndroidAuthorizationList hardware) =>
        hardware.Algorithm == AndroidKeyDescription.AlgorithmEc &&
        (hardware.EcCurve is { } curve ? curve == AndroidKeyDescription.EcCurveP256 : hardware.KeySize == 256);

    /// <summary>Checks the per-login key and extracts the device state it attests to.</summary>
    private MollyAttestationFailure CheckFreshKey(MollyAttestationCertificate leaf, byte[] challenge, out DeviceFacts? facts)
    {
        facts = null;
        AndroidKeyDescription description = leaf.KeyDescription!;
        AndroidAuthorizationList hardware = description.HardwareEnforced;

        if (!CryptographicOperations.FixedTimeEquals(description.AttestationChallenge, challenge))
        {
            return MollyAttestationFailure.ChallengeMismatch;
        }

        if (description.AttestationVersion < MinimumAttestationVersion)
        {
            return MollyAttestationFailure.AttestationVersionTooOld;
        }

        if (!IsHardware(description))
        {
            return MollyAttestationFailure.NotHardwareBacked;
        }

        if (hardware.Origin != AndroidKeyDescription.OriginGenerated)
        {
            return MollyAttestationFailure.KeyNotGeneratedOnDevice;
        }

        if (hardware.RootOfTrust is not { } rootOfTrust)
        {
            return MollyAttestationFailure.MissingRootOfTrust;
        }

        if (!rootOfTrust.DeviceLocked ||
            rootOfTrust.VerifiedBootState is not (AndroidVerifiedBootState.Verified or AndroidVerifiedBootState.SelfSigned) ||
            rootOfTrust.VerifiedBootKey.Length == 0 ||
            !rootOfTrust.VerifiedBootKey.AsSpan().ContainsAnyExcept((byte)0))
        {
            return MollyAttestationFailure.BootloaderUnlocked;
        }

        if (hardware.OsVersion is not { } osVersion || hardware.OsPatchLevel is not { } osPatchLevel ||
            NormalizePatchLevel(osPatchLevel) is not { } normalizedOsPatch)
        {
            return MollyAttestationFailure.MissingOsVersion;
        }

        long? vendorPatch = null;
        long? bootPatch = null;

        // Some implementations report 0 rather than omitting an unset vendor or boot patch level.
        if ((hardware.VendorPatchLevel is > 0 and var vendor && (vendorPatch = NormalizePatchLevel(vendor)) is null) ||
            (hardware.BootPatchLevel is > 0 and var boot && (bootPatch = NormalizePatchLevel(boot)) is null))
        {
            return MollyAttestationFailure.MalformedPatchLevel;
        }

        if (description.ApplicationId is not { Packages: [{ } package], SignatureDigests: [{ } digest] } ||
            !IsAllowedApp(package.Name, digest))
        {
            return MollyAttestationFailure.UnexpectedApplication;
        }

        facts = new DeviceFacts(
            description.AttestationSecurityLevel,
            rootOfTrust,
            package.Name,
            digest,
            package.Version,
            osVersion,
            normalizedOsPatch,
            vendorPatch,
            bootPatch);

        return MollyAttestationFailure.None;
    }

    private static MollyAttestationFailure CheckSameDevice(AndroidKeyDescription description, DeviceFacts facts)
    {
        AndroidRootOfTrust? rootOfTrust = description.HardwareEnforced.RootOfTrust;

        if (rootOfTrust is null ||
            !rootOfTrust.DeviceLocked ||
            rootOfTrust.VerifiedBootState != facts.RootOfTrust.VerifiedBootState ||
            !rootOfTrust.VerifiedBootKey.AsSpan().SequenceEqual(facts.RootOfTrust.VerifiedBootKey))
        {
            return MollyAttestationFailure.PersistentKeyBootMismatch;
        }

        if (description.ApplicationId is not { Packages: [{ } package], SignatureDigests: [{ } digest] } ||
            !string.Equals(package.Name, facts.PackageName, StringComparison.Ordinal) ||
            !digest.AsSpan().SequenceEqual(facts.SignatureDigest))
        {
            return MollyAttestationFailure.PersistentKeyApplicationMismatch;
        }

        return MollyAttestationFailure.None;
    }

    private static MollyAttestationFailure CheckAgainstPinned(DeviceFacts facts, MollyAttestationState pinned)
    {
        if (facts.SecurityLevel != pinned.SecurityLevel)
        {
            return MollyAttestationFailure.SecurityLevelChanged;
        }

        if (facts.RootOfTrust.VerifiedBootState != pinned.VerifiedBootState ||
            !facts.RootOfTrust.VerifiedBootKey.AsSpan().SequenceEqual(pinned.VerifiedBootKey))
        {
            return MollyAttestationFailure.VerifiedBootChanged;
        }

        if (!string.Equals(facts.PackageName, pinned.PackageName, StringComparison.Ordinal) ||
            !facts.SignatureDigest.AsSpan().SequenceEqual(pinned.SignatureDigest))
        {
            return MollyAttestationFailure.ApplicationChanged;
        }

        if (facts.AppVersion < pinned.AppVersion ||
            facts.OsVersion < pinned.OsVersion ||
            facts.OsPatchLevel < pinned.OsPatchLevel ||
            IsRollback(facts.VendorPatchLevel, pinned.VendorPatchLevel) ||
            IsRollback(facts.BootPatchLevel, pinned.BootPatchLevel))
        {
            return MollyAttestationFailure.VersionRollback;
        }

        return MollyAttestationFailure.None;
    }

    /// <summary>A field that was reported before can't disappear to dodge the rollback check.</summary>
    private static bool IsRollback(long? current, long? highWaterMark) =>
        highWaterMark is { } previous && (current is not { } value || value < previous);

    private static bool IsHardware(AndroidKeyDescription description) =>
        description.AttestationSecurityLevel is AndroidSecurityLevel.TrustedEnvironment or AndroidSecurityLevel.StrongBox &&
        description.KeyMintSecurityLevel == description.AttestationSecurityLevel;

    /// <summary>
    /// A single signing certificate digest: Keystore reports the oldest certificate for apps that rotated
    /// their key, so only apps signed by several certificates at once would report more.
    /// </summary>
    private bool IsAllowedApp(string packageName, byte[] digest)
    {
        if (!IsValidPackageName(packageName) || digest.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        // Without an allow list any well-formed app identity is accepted, and pinned at enrollment.
        return _apps.Length == 0 ||
            _apps.Any(app => string.Equals(app.PackageName, packageName, StringComparison.Ordinal) && app.SignatureDigest.AsSpan().SequenceEqual(digest));
    }

    /// <summary>
    /// Android's rules for an app's package name: at least two dot-separated segments, each starting with
    /// an ASCII letter and otherwise made of ASCII letters, digits and underscores.
    /// </summary>
    internal static bool IsValidPackageName(string name)
    {
        string[] segments = name.Split('.');

        return segments.Length >= 2 && segments.All(segment =>
            segment.Length > 0 &&
            char.IsAsciiLetter(segment[0]) &&
            !segment.AsSpan().ContainsAnyExcept(s_packageNameChars));
    }

    /// <summary>
    /// OS patch levels are <c>YYYYMM</c>, vendor and boot patch levels usually <c>YYYYMMDD</c>.
    /// Both are normalized to <c>YYYYMMDD</c> so they stay comparable if a device switches formats.
    /// </summary>
    internal static long? NormalizePatchLevel(long value)
    {
        long normalized = value is >= 100_000 and <= 999_999 ? value * 100 : value;

        long month = normalized / 100 % 100;
        long day = normalized % 100;

        return normalized is >= 10_000_000 and <= 99_999_999 && month is >= 1 and <= 12 && day <= 31
            ? normalized
            : null;
    }

    private static bool VerifyTranscript(MollyAttestationEvidence evidence, ReadOnlySpan<byte> keyHash, MollyExchangeContext exchange, byte[] signingPublicKey, byte[]? attestPublicKey)
    {
        if (evidence.Signature.Length is 0 or > MaxSignatureLength)
        {
            return false;
        }

        byte[] transcript = MollyAttestationTranscript.Build(evidence.Mode, exchange, keyHash, signingPublicKey, attestPublicKey, evidence.FreshChain, evidence.SigningKeyChain, evidence.AttestKeyChain);

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(signingPublicKey, out int read);

            if (read != signingPublicKey.Length ||
                key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                return false;
            }

            return key.VerifyData(transcript, evidence.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The issuing certificates between the device-generated leaf and the root.</summary>
    private static string[] GetPinnedSerials(MollyAttestationCertificate[] chain) =>
        [.. chain[1..^1].Select(c => c.RevocationSerial)];

    private sealed record DeviceFacts(AndroidSecurityLevel SecurityLevel, AndroidRootOfTrust RootOfTrust, string PackageName, byte[] SignatureDigest, long AppVersion, long OsVersion, long OsPatchLevel, long? VendorPatchLevel, long? BootPatchLevel);
}
