using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;

#nullable enable

namespace MihuBot.Molly.Attestation;

public enum AndroidSecurityLevel
{
    Software = 0,
    TrustedEnvironment = 1,
    StrongBox = 2,
}

public enum AndroidVerifiedBootState
{
    Verified = 0,
    SelfSigned = 1,
    Unverified = 2,
    Failed = 3,
}

public sealed record AndroidRootOfTrust(byte[] VerifiedBootKey, bool DeviceLocked, AndroidVerifiedBootState VerifiedBootState);

/// <summary>The app the key belongs to, as reported by the OS (<c>AttestationApplicationId</c>).</summary>
public sealed record AndroidAttestationApplicationId(AndroidPackageInfo[] Packages, byte[][] SignatureDigests);

public sealed record AndroidPackageInfo(string Name, long Version);

/// <summary>The subset of a KeyMint <c>AuthorizationList</c> that the Molly verifier relies on.</summary>
public sealed class AndroidAuthorizationList
{
    // https://source.android.com/docs/security/features/keystore/attestation#schema
    private const int PurposeTag = 1;
    private const int AlgorithmTag = 2;
    private const int KeySizeTag = 3;
    private const int EcCurveTag = 10;
    private const int OriginTag = 702;
    private const int RootOfTrustTag = 704;
    private const int OsVersionTag = 705;
    private const int OsPatchLevelTag = 706;
    private const int AttestationApplicationIdTag = 709;
    private const int VendorPatchLevelTag = 718;
    private const int BootPatchLevelTag = 719;

    private const int MaxSetValues = 16;
    private const int MaxPackages = 16;
    private const int MaxSignatureDigests = 16;
    private const int MaxPackageNameLength = 256;
    private const int MaxVerifiedBootKeyLength = 64;

    public int[]? Purposes { get; private set; }

    public int? Algorithm { get; private set; }

    public int? KeySize { get; private set; }

    public int? EcCurve { get; private set; }

    public int? Origin { get; private set; }

    public AndroidRootOfTrust? RootOfTrust { get; private set; }

    public long? OsVersion { get; private set; }

    public long? OsPatchLevel { get; private set; }

    public long? VendorPatchLevel { get; private set; }

    public long? BootPatchLevel { get; private set; }

    public AndroidAttestationApplicationId? AttestationApplicationId { get; private set; }

    internal static AndroidAuthorizationList Read(AsnReader parent)
    {
        AsnReader reader = parent.ReadSequence();
        var list = new AndroidAuthorizationList();
        var seen = new HashSet<int>();

        while (reader.HasData)
        {
            Asn1Tag tag = reader.PeekTag();

            if (tag.TagClass != TagClass.ContextSpecific || !seen.Add(tag.TagValue))
            {
                throw new AsnContentException("Unexpected or duplicate authorization list tag.");
            }

            // Fields the verifier doesn't use are skipped however they're encoded, so an unusual encoding
            // of an irrelevant field can't lock a device out. Used fields must be EXPLICIT (constructed).
            if (!IsUsedTag(tag.TagValue))
            {
                reader.ReadEncodedValue();
                continue;
            }

            if (!tag.IsConstructed)
            {
                throw new AsnContentException("Unexpected primitive authorization list tag.");
            }

            switch (tag.TagValue)
            {
                case PurposeTag:
                    list.Purposes = ReadExplicit(reader, tag, ReadIntegerSet);
                    break;

                case AlgorithmTag:
                    list.Algorithm = ReadExplicit(reader, tag, ReadInt32);
                    break;

                case KeySizeTag:
                    list.KeySize = ReadExplicit(reader, tag, ReadInt32);
                    break;

                case EcCurveTag:
                    list.EcCurve = ReadExplicit(reader, tag, ReadInt32);
                    break;

                case OriginTag:
                    list.Origin = ReadExplicit(reader, tag, ReadInt32);
                    break;

                case RootOfTrustTag:
                    list.RootOfTrust = ReadExplicit(reader, tag, ReadRootOfTrust);
                    break;

                case OsVersionTag:
                    list.OsVersion = ReadExplicit(reader, tag, ReadNonNegativeInt64);
                    break;

                case OsPatchLevelTag:
                    list.OsPatchLevel = ReadExplicit(reader, tag, ReadNonNegativeInt64);
                    break;

                case AttestationApplicationIdTag:
                    list.AttestationApplicationId = ReadExplicit(reader, tag, ReadApplicationId);
                    break;

                case VendorPatchLevelTag:
                    list.VendorPatchLevel = ReadExplicit(reader, tag, ReadNonNegativeInt64);
                    break;

                case BootPatchLevelTag:
                    list.BootPatchLevel = ReadExplicit(reader, tag, ReadNonNegativeInt64);
                    break;

                default:
                    throw new UnreachableException();
            }
        }

        return list;
    }

    private static bool IsUsedTag(int tag) => tag is PurposeTag or AlgorithmTag or KeySizeTag or EcCurveTag or OriginTag or RootOfTrustTag or OsVersionTag or OsPatchLevelTag or AttestationApplicationIdTag or VendorPatchLevelTag or BootPatchLevelTag;

    private static T ReadExplicit<T>(AsnReader reader, Asn1Tag tag, Func<AsnReader, T> read)
    {
        AsnReader inner = reader.ReadSequence(tag);
        T value = read(inner);
        inner.ThrowIfNotEmpty();
        return value;
    }

    private static int[] ReadIntegerSet(AsnReader reader)
    {
        AsnReader set = reader.ReadSetOf(skipSortOrderValidation: true);
        var values = new List<int>();

        while (set.HasData)
        {
            if (values.Count >= MaxSetValues)
            {
                throw new AsnContentException("Too many values.");
            }

            values.Add(ReadInt32(set));
        }

        return [.. values];
    }

    internal static int ReadInt32(AsnReader reader)
    {
        if (!reader.TryReadInt32(out int value))
        {
            throw new AsnContentException("Integer out of range.");
        }

        return value;
    }

    private static long ReadNonNegativeInt64(AsnReader reader)
    {
        if (!reader.TryReadInt64(out long value) || value < 0)
        {
            throw new AsnContentException("Integer out of range.");
        }

        return value;
    }

    internal static int ReadEnumerated(AsnReader reader)
    {
        // Minimal two's complement, which the reader already enforces. Values are all small and non-negative.
        ReadOnlySpan<byte> bytes = reader.ReadEnumeratedBytes().Span;

        return bytes switch
        {
            [< 0x80] => bytes[0],
            [0, >= 0x80] => bytes[1],
            _ => throw new AsnContentException("Enumerated value out of range."),
        };
    }

    private static AndroidRootOfTrust ReadRootOfTrust(AsnReader reader)
    {
        AsnReader sequence = reader.ReadSequence();

        byte[] verifiedBootKey = sequence.ReadOctetString();
        bool deviceLocked = sequence.ReadBoolean();
        int state = ReadEnumerated(sequence);

        if (verifiedBootKey.Length > MaxVerifiedBootKeyLength || state > (int)AndroidVerifiedBootState.Failed)
        {
            throw new AsnContentException("Invalid root of trust.");
        }

        // verifiedBootHash (v3+) changes with every OS update, so it isn't used.
        if (sequence.HasData)
        {
            sequence.ReadOctetString();
        }

        sequence.ThrowIfNotEmpty();

        return new AndroidRootOfTrust(verifiedBootKey, deviceLocked, (AndroidVerifiedBootState)state);
    }

    private static AndroidAttestationApplicationId ReadApplicationId(AsnReader reader)
    {
        // The value is an OCTET STRING wrapping the DER AttestationApplicationId.
        byte[] encoded = reader.ReadOctetString();
        var inner = new AsnReader(encoded, AsnEncodingRules.BER);
        AsnReader sequence = inner.ReadSequence();
        inner.ThrowIfNotEmpty();

        var packages = new List<AndroidPackageInfo>();
        AsnReader packageSet = sequence.ReadSetOf(skipSortOrderValidation: true);

        while (packageSet.HasData)
        {
            if (packages.Count >= MaxPackages)
            {
                throw new AsnContentException("Too many packages.");
            }

            AsnReader package = packageSet.ReadSequence();
            byte[] name = package.ReadOctetString();
            long version = ReadNonNegativeInt64(package);
            package.ThrowIfNotEmpty();

            if (name.Length is 0 or > MaxPackageNameLength)
            {
                throw new AsnContentException("Invalid package name.");
            }

            packages.Add(new AndroidPackageInfo(Encoding.UTF8.GetString(name), version));
        }

        var digests = new List<byte[]>();
        AsnReader digestSet = sequence.ReadSetOf(skipSortOrderValidation: true);

        while (digestSet.HasData)
        {
            if (digests.Count >= MaxSignatureDigests)
            {
                throw new AsnContentException("Too many signature digests.");
            }

            digests.Add(digestSet.ReadOctetString());
        }

        sequence.ThrowIfNotEmpty();

        return new AndroidAttestationApplicationId([.. packages], [.. digests]);
    }
}

/// <summary>
/// The Android key attestation extension (<c>KeyDescription</c>, OID 1.3.6.1.4.1.11129.2.1.17).
/// </summary>
public sealed class AndroidKeyDescription
{
    public const string ExtensionOid = "1.3.6.1.4.1.11129.2.1.17";

    public const int PurposeSign = 2;
    public const int PurposeVerify = 3;
    public const int PurposeAttestKey = 7;
    public const int AlgorithmEc = 3;
    public const int EcCurveP256 = 1;
    public const int OriginGenerated = 0;

    private const int MaxChallengeLength = 128;

    public required int AttestationVersion { get; init; }

    public required AndroidSecurityLevel AttestationSecurityLevel { get; init; }

    public required int KeyMintVersion { get; init; }

    public required AndroidSecurityLevel KeyMintSecurityLevel { get; init; }

    public required byte[] AttestationChallenge { get; init; }

    public required AndroidAuthorizationList SoftwareEnforced { get; init; }

    public required AndroidAuthorizationList HardwareEnforced { get; init; }

    /// <summary>
    /// The application id is supplied by the OS, so KeyMint lists it as software-enforced.
    /// Null if it's missing or ambiguously present in both lists.
    /// </summary>
    public AndroidAttestationApplicationId? ApplicationId =>
        SoftwareEnforced.AttestationApplicationId is { } software
            ? HardwareEnforced.AttestationApplicationId is null ? software : null
            : HardwareEnforced.AttestationApplicationId;

    /// <summary>Parses the extension value. Any malformed or unexpected structure fails.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> extensionValue, [NotNullWhen(true)] out AndroidKeyDescription? description)
    {
        description = null;

        try
        {
            // Real devices don't always produce strictly sorted DER SET OFs, so BER is accepted.
            // The value is covered by the certificate signature, and the input size is bounded.
            var reader = new AsnReader(extensionValue, AsnEncodingRules.BER);
            AsnReader sequence = reader.ReadSequence();
            reader.ThrowIfNotEmpty();

            int attestationVersion = AndroidAuthorizationList.ReadInt32(sequence);
            int attestationSecurityLevel = AndroidAuthorizationList.ReadEnumerated(sequence);
            int keyMintVersion = AndroidAuthorizationList.ReadInt32(sequence);
            int keyMintSecurityLevel = AndroidAuthorizationList.ReadEnumerated(sequence);
            byte[] challenge = sequence.ReadOctetString();

            // uniqueId is not used.
            sequence.ReadOctetString();

            AndroidAuthorizationList softwareEnforced = AndroidAuthorizationList.Read(sequence);
            AndroidAuthorizationList hardwareEnforced = AndroidAuthorizationList.Read(sequence);
            sequence.ThrowIfNotEmpty();

            if (attestationSecurityLevel > (int)AndroidSecurityLevel.StrongBox ||
                keyMintSecurityLevel > (int)AndroidSecurityLevel.StrongBox ||
                challenge.Length > MaxChallengeLength)
            {
                return false;
            }

            description = new AndroidKeyDescription
            {
                AttestationVersion = attestationVersion,
                AttestationSecurityLevel = (AndroidSecurityLevel)attestationSecurityLevel,
                KeyMintVersion = keyMintVersion,
                KeyMintSecurityLevel = (AndroidSecurityLevel)keyMintSecurityLevel,
                AttestationChallenge = challenge,
                SoftwareEnforced = softwareEnforced,
                HardwareEnforced = hardwareEnforced,
            };

            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}
