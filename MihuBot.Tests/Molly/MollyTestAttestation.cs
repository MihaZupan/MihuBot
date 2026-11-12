using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Attestation;

namespace MihuBot.Tests.Molly;

/// <summary>
/// A stand-in for the Android attestation PKI: a root and intermediate that play the manufacturer's
/// role, trusted only by tests through <see cref="TrustAnchors"/>.
/// </summary>
public sealed class MollyTestPki
{
    public static MollyTestPki Instance { get; } = new();

    /// <summary>The app the test devices run, which the test servers are configured to allow.</summary>
    public const string PackageName = "org.example.molly.test";

    public static byte[] SignatureDigest { get; } = SHA256.HashData("molly-test-signer"u8);

    public static MollyAttestationApp App => new(PackageName, SignatureDigest);

    /// <summary>The <c>Molly:AttestationApps</c> configuration value for <see cref="App"/>.</summary>
    public static string AppsConfiguration => $"{PackageName}:{Convert.ToHexString(SignatureDigest)}";

    private MollyTestPki()
    {
        var rootRequest = new CertificateRequest(RootName, RootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(10));
        Root = root.RawData;

        Intermediate = IssueCertificate(IntermediateKey, IntermediateName, RootName, RootKey, keyDescription: null, ca: true);
    }

    public X500DistinguishedName RootName { get; } = new("CN=Molly Test Attestation Root");

    public static X500DistinguishedName LeafName { get; } = new("CN=Android Keystore Key");

    public ECDsa RootKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ECDsa IntermediateKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public X500DistinguishedName IntermediateName { get; } = new("CN=Molly Test Attestation Intermediate");

    public byte[] Root { get; }

    public byte[] Intermediate { get; }

    public MollyAttestationTrustAnchors TrustAnchors => new([RootKey.ExportSubjectPublicKeyInfo()]);

    /// <summary>Like <see cref="IssueManufacturerChain"/>, but through a new intermediate directly below the root.</summary>
    public byte[][] IssueChainThroughNewIntermediate(ECDsa subjectKey, byte[] keyDescription, X500DistinguishedName intermediateName, DateTimeOffset? intermediateNotAfter = null, X509Extension? intermediateExtension = null)
    {
        using ECDsa intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] intermediate = IssueCertificate(intermediateKey, intermediateName, RootName, RootKey, keyDescription: null, ca: true, notAfter: intermediateNotAfter, extension: intermediateExtension);
        return [IssueCertificate(subjectKey, LeafName, intermediateName, intermediateKey, keyDescription), intermediate, Root];
    }

    /// <summary>A leaf issued by the "manufacturer", followed by the intermediate and root.</summary>
    public byte[][] IssueManufacturerChain(ECDsa subjectKey, MollyTestKeyDescription description, DateTimeOffset? notAfter = null) =>
        [IssueCertificate(subjectKey, new X500DistinguishedName("CN=Android Keystore Key"), IntermediateName, IntermediateKey, description.Encode(), notAfter: notAfter), Intermediate, Root];

    public static byte[] IssueCertificate(ECDsa subjectKey, X500DistinguishedName subject, X500DistinguishedName issuer, ECDsa issuerKey, byte[]? keyDescription, bool ca = false, DateTimeOffset? notAfter = null, X509Extension? extension = null) =>
        IssueCertificate(new CertificateRequest(subject, subjectKey, HashAlgorithmName.SHA256), issuer, X509SignatureGenerator.CreateForECDsa(issuerKey), keyDescription, ca, notAfter, extension);

    public static byte[] IssueCertificate(CertificateRequest request, X500DistinguishedName issuer, X509SignatureGenerator issuerSigner, byte[]? keyDescription, bool ca = false, DateTimeOffset? notAfter = null, X509Extension? extension = null)
    {
        if (extension is not null)
        {
            request.CertificateExtensions.Add(extension);
        }


        if (keyDescription is not null)
        {
            request.CertificateExtensions.Add(new X509Extension(AndroidKeyDescription.ExtensionOid, keyDescription, critical: false));
        }

        if (ca)
        {
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        }

        byte[] serial = RandomNumberGenerator.GetBytes(8);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01);

        using X509Certificate2 certificate = request.Create(issuer, issuerSigner, DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(5), serial);
        return certificate.RawData;
    }

    /// <summary>The serial as the Android revocation list spells it.</summary>
    public static string RevocationSerial(byte[] certificate)
    {
        using var parsed = X509CertificateLoader.LoadCertificate(certificate);
        return MollyAttestationRevocations.NormalizeSerial(parsed.SerialNumber)!;
    }
}

/// <summary>Encodes an Android <c>KeyDescription</c> attestation extension.</summary>
public sealed record MollyTestKeyDescription
{
    public int AttestationVersion { get; init; } = 300;

    public AndroidSecurityLevel SecurityLevel { get; init; } = AndroidSecurityLevel.TrustedEnvironment;

    /// <summary>Defaults to <see cref="SecurityLevel"/>.</summary>
    public AndroidSecurityLevel? KeyMintSecurityLevel { get; init; }

    public byte[] Challenge { get; init; } = [];

    public int[]? Purposes { get; init; } = [AndroidKeyDescription.PurposeSign];

    public int? Algorithm { get; init; } = AndroidKeyDescription.AlgorithmEc;

    public int? EcCurve { get; init; } = AndroidKeyDescription.EcCurveP256;

    public int? Origin { get; init; } = AndroidKeyDescription.OriginGenerated;

    public bool IncludeRootOfTrust { get; init; } = true;

    public byte[] VerifiedBootKey { get; init; } = new byte[32];

    public bool DeviceLocked { get; init; } = true;

    public AndroidVerifiedBootState VerifiedBootState { get; init; } = AndroidVerifiedBootState.Verified;

    public long? OsVersion { get; init; } = 160000;

    public long? OsPatchLevel { get; init; } = 202610;

    public long? VendorPatchLevel { get; init; } = 20261005;

    public long? BootPatchLevel { get; init; } = 20261005;

    public bool IncludeApplicationId { get; init; } = true;

    /// <summary>Adds an unused field, encoded as a primitive (IMPLICIT) value rather than EXPLICIT.</summary>
    public bool IncludeUnusedPrimitiveField { get; init; }

    public string[] PackageNames { get; init; } = [MollyTestPki.PackageName];

    public long AppVersion { get; init; } = 100;

    public byte[][] SignatureDigests { get; init; } = [MollyTestPki.SignatureDigest];

    public byte[] Encode()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            writer.WriteInteger(AttestationVersion);
            writer.WriteEnumeratedValue(SecurityLevel);
            writer.WriteInteger(400);
            writer.WriteEnumeratedValue(KeyMintSecurityLevel ?? SecurityLevel);
            writer.WriteOctetString(Challenge);
            writer.WriteOctetString([]);

            using (writer.PushSequence())
            {
                if (IncludeApplicationId)
                {
                    WriteExplicit(writer, 709, w => w.WriteOctetString(EncodeApplicationId()));
                }
            }

            using (writer.PushSequence())
            {
                if (Purposes is not null)
                {
                    WriteExplicit(writer, 1, w =>
                    {
                        using (w.PushSetOf())
                        {
                            foreach (int purpose in Purposes)
                            {
                                w.WriteInteger(purpose);
                            }
                        }
                    });
                }

                WriteOptionalInteger(writer, 2, Algorithm);
                WriteOptionalInteger(writer, 3, 256);
                WriteOptionalInteger(writer, 10, EcCurve);
                WriteOptionalInteger(writer, 702, Origin);

                if (IncludeRootOfTrust)
                {
                    WriteExplicit(writer, 704, w =>
                    {
                        using (w.PushSequence())
                        {
                            w.WriteOctetString(VerifiedBootKey);
                            w.WriteBoolean(DeviceLocked);
                            w.WriteEnumeratedValue(VerifiedBootState);
                            w.WriteOctetString(new byte[32]);
                        }
                    });
                }

                if (IncludeUnusedPrimitiveField)
                {
                    writer.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 503));
                }

                WriteOptionalInteger(writer, 705, OsVersion);
                WriteOptionalInteger(writer, 706, OsPatchLevel);
                WriteOptionalInteger(writer, 718, VendorPatchLevel);
                WriteOptionalInteger(writer, 719, BootPatchLevel);
            }
        }

        return writer.Encode();
    }

    private byte[] EncodeApplicationId()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            using (writer.PushSetOf())
            {
                foreach (string packageName in PackageNames)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteOctetString(Encoding.UTF8.GetBytes(packageName));
                        writer.WriteInteger(AppVersion);
                    }
                }
            }

            using (writer.PushSetOf())
            {
                foreach (byte[] digest in SignatureDigests)
                {
                    writer.WriteOctetString(digest);
                }
            }
        }

        return writer.Encode();
    }

    private static void WriteOptionalInteger(AsnWriter writer, int tag, long? value)
    {
        if (value is { } integer)
        {
            WriteExplicit(writer, tag, w => w.WriteInteger(integer));
        }
    }

    private static void WriteExplicit(AsnWriter writer, int tag, Action<AsnWriter> write)
    {
        var explicitTag = new Asn1Tag(TagClass.ContextSpecific, tag, isConstructed: true);

        using (writer.PushSequence(explicitTag))
        {
            write(writer);
        }
    }
}

/// <summary>Options for deliberately producing bad or unusual proofs.</summary>
public sealed record MollyTestProofOptions
{
    /// <summary>A real client sends its enrollment chains until it knows it enrolled.</summary>
    public bool IncludeEnrollmentEvidence { get; init; } = true;

    public Func<MollyTestKeyDescription, MollyTestKeyDescription>? Fresh { get; init; }

    /// <summary>In strict mode, issue the fresh key from the manufacturer chain instead of the attest key.</summary>
    public bool FreshFromManufacturer { get; init; }

    /// <summary>Overrides the key that signs the fresh key in strict mode.</summary>
    public ECDsa? FreshIssuer { get; init; }

    public MollyAttestationMode? ClaimedMode { get; init; }

    public ECDsa? SignWith { get; init; }

    /// <summary>Signs a transcript for a different exchange than the one the proof is sent in.</summary>
    public MollyExchangeContext? SignedExchange { get; init; }

    public byte[]? SignedKeyHash { get; init; }

    public Func<byte[][], byte[][]>? TamperFreshChain { get; init; }
}

/// <summary>
/// A simulated Android device: persistent hardware keys created once, and fresh attested keys per login,
/// all issued through <see cref="MollyTestPki"/>.
/// </summary>
public sealed class MollyTestDevice
{
    public MollyTestDevice(MollyAttestationMode mode = MollyAttestationMode.Strict, AndroidSecurityLevel securityLevel = AndroidSecurityLevel.TrustedEnvironment, byte[]? verifiedBootKey = null, AndroidVerifiedBootState verifiedBootState = AndroidVerifiedBootState.Verified, string? packageName = null, byte[]? signatureDigest = null)
    {
        Mode = mode;
        SecurityLevel = securityLevel;
        VerifiedBootKey = verifiedBootKey ?? RandomNumberGenerator.GetBytes(32);
        VerifiedBootState = verifiedBootState;
        PackageName = packageName ?? MollyTestPki.PackageName;
        SignatureDigest = signatureDigest ?? MollyTestPki.SignatureDigest;

        MollyTestPki pki = MollyTestPki.Instance;
        // Persistent keys get a non-empty attestation challenge, as some implementations require one.
        MollyTestKeyDescription signingDescription = Describe([1]) with { Purposes = [AndroidKeyDescription.PurposeSign, AndroidKeyDescription.PurposeVerify] };

        if (mode == MollyAttestationMode.Strict)
        {
            AttestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            AttestKeyChain = pki.IssueManufacturerChain(AttestKey, Describe([1]) with { Purposes = [AndroidKeyDescription.PurposeAttestKey] });
            SigningKeyChain = [IssueFromAttestKey(SigningKey, signingDescription, AttestKey)];
        }
        else
        {
            AttestKeyChain = [];
            SigningKeyChain = pki.IssueManufacturerChain(SigningKey, signingDescription);
        }
    }

    public MollyAttestationMode Mode { get; }

    public AndroidSecurityLevel SecurityLevel { get; }

    public byte[] VerifiedBootKey { get; set; }

    public AndroidVerifiedBootState VerifiedBootState { get; set; }

    public string PackageName { get; }

    public byte[] SignatureDigest { get; }

    public long AppVersion { get; set; } = 100;

    public long OsVersion { get; set; } = 160000;

    public long OsPatchLevel { get; set; } = 202610;

    public long? VendorPatchLevel { get; set; } = 20261005;

    public long? BootPatchLevel { get; set; } = 20261005;

    public ECDsa SigningKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ECDsa? AttestKey { get; }

    public byte[][] SigningKeyChain { get; set; }

    public byte[][] AttestKeyChain { get; set; }

    /// <summary>The device's current state, as its KeyMint would attest it.</summary>
    public MollyTestKeyDescription Describe(byte[] challenge) => new()
    {
        SecurityLevel = SecurityLevel,
        Challenge = challenge,
        VerifiedBootKey = VerifiedBootKey,
        VerifiedBootState = VerifiedBootState,
        PackageNames = [PackageName],
        SignatureDigests = [SignatureDigest],
        AppVersion = AppVersion,
        OsVersion = OsVersion,
        OsPatchLevel = OsPatchLevel,
        VendorPatchLevel = VendorPatchLevel,
        BootPatchLevel = BootPatchLevel,
    };

    public MollyAttestationProof CreateProof(byte[] keyHash, MollyExchangeContext exchange, MollyTestProofOptions? options = null)
    {
        options ??= new MollyTestProofOptions();
        MollyAttestationMode mode = options.ClaimedMode ?? Mode;
        byte[] challenge = MollyAttestationTranscript.Challenge(exchange);

        MollyTestKeyDescription freshDescription = Describe(challenge);
        freshDescription = options.Fresh?.Invoke(freshDescription) ?? freshDescription;

        using ECDsa freshKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        byte[][] freshChain = Mode == MollyAttestationMode.Strict && !options.FreshFromManufacturer
            ? [IssueFromAttestKey(freshKey, freshDescription, options.FreshIssuer ?? AttestKey!)]
            : MollyTestPki.Instance.IssueManufacturerChain(freshKey, freshDescription);

        freshChain = options.TamperFreshChain?.Invoke(freshChain) ?? freshChain;

        byte[][] signingKeyChain = options.IncludeEnrollmentEvidence ? SigningKeyChain : [];
        byte[][] attestKeyChain = options.IncludeEnrollmentEvidence ? AttestKeyChain : [];

        byte[] transcript = MollyAttestationTranscript.Build(mode, options.SignedExchange ?? exchange, options.SignedKeyHash ?? keyHash,
            SigningKey.ExportSubjectPublicKeyInfo(), AttestKey?.ExportSubjectPublicKeyInfo() ?? [], freshChain, signingKeyChain, attestKeyChain);

        byte[] signature = (options.SignWith ?? SigningKey).SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new MollyAttestationProof
        {
            Version = MollyAttestationService.ProtocolVersion,
            Mode = mode.ToWireValue(),
            FreshChain = [.. freshChain.Select(Convert.ToBase64String)],
            SigningKeyChain = signingKeyChain.Length == 0 ? null : [.. signingKeyChain.Select(Convert.ToBase64String)],
            AttestKeyChain = attestKeyChain.Length == 0 ? null : [.. attestKeyChain.Select(Convert.ToBase64String)],
            Signature = Convert.ToBase64String(signature),
        };
    }

    private static byte[] IssueFromAttestKey(ECDsa subjectKey, MollyTestKeyDescription description, ECDsa attestKey) =>
        MollyTestPki.IssueCertificate(subjectKey, new X500DistinguishedName("CN=Android Keystore Key"), new X500DistinguishedName("CN=Molly Attest Key"), attestKey, description.Encode());

    /// <summary>A plausible transport header, for calling the service directly.</summary>
    public static MollyExchangeContext NewExchange(TimeSpan recipientKeyAge = default) => new(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(MollyRequestProtector.RequestNonceLength), recipientKeyAge);
}
