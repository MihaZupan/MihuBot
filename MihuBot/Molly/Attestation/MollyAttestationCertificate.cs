using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Molly.Attestation;

/// <summary>A parsed certificate from a client-supplied attestation chain.</summary>
/// <remarks>
/// Parsed with <see cref="AsnReader"/> rather than <c>X509Certificate2</c>, whose parsing is platform-specific.
/// Signatures cover the TBSCertificate exactly as the device encoded it, so devices with non-canonical
/// encodings still verify.
/// </remarks>
internal sealed class MollyAttestationCertificate
{
    public const int MaxEncodedLength = 4096;

    /// <summary>Present in certificates issued through Remote Key Provisioning, which must not be trusted past their expiry.</summary>
    public const string ProvisioningInfoOid = "1.3.6.1.4.1.11129.2.1.30";

    private const string SerialNumberAttributeOid = "2.5.4.5";
    private const string RsaKeyOid = "1.2.840.113549.1.1.1";
    private const string EcKeyOid = "1.2.840.10045.2.1";

    /// <summary>The signature algorithms Android attestation chains use. Anything else is rejected.</summary>
    private static readonly FrozenDictionary<string, (bool IsRsa, HashAlgorithmName Hash)> s_signatureAlgorithms = new Dictionary<string, (bool, HashAlgorithmName)>
    {
        ["1.2.840.113549.1.1.11"] = (true, HashAlgorithmName.SHA256),  // sha256WithRSAEncryption
        ["1.2.840.113549.1.1.12"] = (true, HashAlgorithmName.SHA384),  // sha384WithRSAEncryption
        ["1.2.840.113549.1.1.13"] = (true, HashAlgorithmName.SHA512),  // sha512WithRSAEncryption
        ["1.2.840.10045.4.3.2"] = (false, HashAlgorithmName.SHA256),   // ecdsa-with-SHA256
        ["1.2.840.10045.4.3.3"] = (false, HashAlgorithmName.SHA384),   // ecdsa-with-SHA384
        ["1.2.840.10045.4.3.4"] = (false, HashAlgorithmName.SHA512),   // ecdsa-with-SHA512
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> s_allowedCurves = FrozenSet.Create(StringComparer.Ordinal,
        "1.2.840.10045.3.1.7", // P-256
        "1.3.132.0.34",        // P-384
        "1.3.132.0.35");       // P-521

    private readonly byte[] _signedBytes;
    private readonly byte[] _signature;
    private readonly string _signatureAlgorithm;
    private readonly Name _issuer;
    private readonly Name _subject;
    private readonly DateTimeOffset _notBefore;
    private readonly DateTimeOffset _notAfter;

    private MollyAttestationCertificate(byte[] encoded, byte[] signedBytes, byte[] signature, string signatureAlgorithm, string revocationSerial, Name issuer, Name subject, DateTimeOffset notBefore, DateTimeOffset notAfter, byte[] subjectPublicKeyInfo, AndroidKeyDescription? keyDescription, bool hasKeyDescriptionExtension, bool hasProvisioningInfo)
    {
        Encoded = encoded;
        SubjectPublicKeyInfo = subjectPublicKeyInfo;
        KeyDescription = keyDescription;
        HasKeyDescriptionExtension = hasKeyDescriptionExtension;
        HasProvisioningInfo = hasProvisioningInfo;
        RevocationSerial = revocationSerial;
        _signedBytes = signedBytes;
        _signature = signature;
        _signatureAlgorithm = signatureAlgorithm;
        _issuer = issuer;
        _subject = subject;
        _notBefore = notBefore;
        _notAfter = notAfter;
    }

    public byte[] Encoded { get; }

    /// <summary>The DER SubjectPublicKeyInfo.</summary>
    public byte[] SubjectPublicKeyInfo { get; }

    /// <summary>The parsed attestation extension, if present and well-formed.</summary>
    public AndroidKeyDescription? KeyDescription { get; }

    /// <summary>True even if the extension failed to parse - its presence alone matters for topology checks.</summary>
    public bool HasKeyDescriptionExtension { get; }

    /// <summary>Whether this certificate was issued through Remote Key Provisioning.</summary>
    public bool HasProvisioningInfo { get; }

    /// <summary>Factory-provisioned intermediates name a serial number, unlike the RKP ones (e.g. <c>CN=Droid CA2</c>).</summary>
    public bool HasSerialNumberInSubject => _subject.Any(rdn => rdn.Any(attribute => attribute.Type == SerialNumberAttributeOid));

    /// <summary>Whether this certificate names <paramref name="issuer"/>'s subject as its issuer.</summary>
    public bool IsIssuedBy(MollyAttestationCertificate issuer) => NamesMatch(_issuer, issuer._subject);

    /// <summary>The serial number as lowercase hex without leading zeros. See <see cref="MollyRevocationSnapshot.IsRevoked"/>.</summary>
    public string RevocationSerial { get; }

    public static bool TryParse(byte[] encoded, [NotNullWhen(true)] out MollyAttestationCertificate? certificate)
    {
        certificate = null;

        if (encoded.Length is 0 or > MaxEncodedLength)
        {
            return false;
        }

        try
        {
            var reader = new AsnReader(encoded, AsnEncodingRules.BER);
            AsnReader outer = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            byte[] signedBytes = outer.PeekEncodedValue().ToArray();
            AsnReader tbs = outer.ReadSequence();
            ReadOnlyMemory<byte> outerAlgorithm = outer.ReadEncodedValue();
            byte[] signature = outer.ReadBitString(out int unusedBits);
            outer.ThrowIfNotEmpty();

            if (unusedBits != 0)
            {
                return false;
            }

            if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                AsnReader version = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                version.ReadInteger();
                version.ThrowIfNotEmpty();
            }

            string revocationSerial = FormatSerial(tbs.ReadIntegerBytes().Span);
            ReadOnlyMemory<byte> tbsAlgorithm = tbs.ReadEncodedValue();

            if (!tbsAlgorithm.Span.SequenceEqual(outerAlgorithm.Span))
            {
                return false;
            }

            string signatureAlgorithm = ReadAlgorithmIdentifier(tbsAlgorithm, out _);

            if (!s_signatureAlgorithms.ContainsKey(signatureAlgorithm))
            {
                return false;
            }

            Name issuer = ReadName(tbs);
            AsnReader validity = tbs.ReadSequence();
            DateTimeOffset notBefore = ReadTime(validity);
            DateTimeOffset notAfter = ReadTime(validity);
            validity.ThrowIfNotEmpty();
            Name subject = ReadName(tbs);
            byte[] subjectPublicKeyInfo = NormalizeSubjectPublicKeyInfo(tbs.ReadEncodedValue());

            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
            {
                tbs.ReadEncodedValue();
            }

            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
            {
                tbs.ReadEncodedValue();
            }

            AndroidKeyDescription? keyDescription = null;
            bool hasKeyDescriptionExtension = false;
            bool hasProvisioningInfo = false;

            if (tbs.HasData)
            {
                AsnReader extensionsWrapper = tbs.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3));
                AsnReader extensions = extensionsWrapper.ReadSequence();
                extensionsWrapper.ThrowIfNotEmpty();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                while (extensions.HasData)
                {
                    AsnReader extension = extensions.ReadSequence();
                    string oid = extension.ReadObjectIdentifier();

                    if (extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
                    {
                        extension.ReadBoolean();
                    }

                    byte[] value = extension.ReadOctetString();
                    extension.ThrowIfNotEmpty();

                    if (!seen.Add(oid))
                    {
                        return false;
                    }

                    if (oid == AndroidKeyDescription.ExtensionOid)
                    {
                        hasKeyDescriptionExtension = true;

                        if (AndroidKeyDescription.TryParse(value, out AndroidKeyDescription? description))
                        {
                            keyDescription = description;
                        }
                    }
                    else if (oid == ProvisioningInfoOid)
                    {
                        hasProvisioningInfo = true;
                    }
                }
            }

            tbs.ThrowIfNotEmpty();

            certificate = new MollyAttestationCertificate(encoded, signedBytes, signature, signatureAlgorithm, revocationSerial, issuer, subject, notBefore, notAfter, subjectPublicKeyInfo, keyDescription, hasKeyDescriptionExtension, hasProvisioningInfo);
            return true;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks that this certificate was signed by the holder of <paramref name="issuerSubjectPublicKeyInfo"/>.
    /// Issuer keys outside the sizes Android chains use are rejected before doing any expensive work.
    /// </summary>
    public bool IsSignedBy(byte[] issuerSubjectPublicKeyInfo)
    {
        (bool isRsa, HashAlgorithmName hash) = s_signatureAlgorithms[_signatureAlgorithm];

        try
        {
            string keyAlgorithm = ReadAlgorithmIdentifier(issuerSubjectPublicKeyInfo, out ReadOnlyMemory<byte>? keyParameters, subjectPublicKeyInfo: true);

            if (isRsa && keyAlgorithm == RsaKeyOid)
            {
                using RSA rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(issuerSubjectPublicKeyInfo, out int read);
                RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);

                return read == issuerSubjectPublicKeyInfo.Length &&
                    rsa.KeySize is >= 2048 and <= 4096 &&
                    parameters.Exponent!.AsSpan().TrimStart((byte)0).Length <= 4 &&
                    rsa.VerifyData(_signedBytes, _signature, hash, RSASignaturePadding.Pkcs1);
            }

            if (!isRsa && keyAlgorithm == EcKeyOid && keyParameters is { } curve && s_allowedCurves.Contains(AsnDecoder.ReadObjectIdentifier(curve.Span, AsnEncodingRules.DER, out _)))
            {
                using ECDsa ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(issuerSubjectPublicKeyInfo, out int read);

                return read == issuerSubjectPublicKeyInfo.Length &&
                    ecdsa.VerifyData(_signedBytes, _signature, hash, DSASignatureFormat.Rfc3279DerSequence);
            }

            return false;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="now"/> is within the validity period, allowing for some clock skew.</summary>
    /// <param name="enforceExpiry">False to only check that the certificate is already valid.</param>
    public bool IsValidAt(DateTimeOffset now, TimeSpan skew, bool enforceExpiry = true) =>
        now >= _notBefore - skew && (!enforceExpiry || now <= _notAfter + skew);

    /// <summary>
    /// Matches the revocation list's spelling: lowercase hex without leading zeros. Read as unsigned, so a
    /// non-canonical serial with its high bit set keeps all of its digits instead of colliding with a shorter one.
    /// </summary>
    private static string FormatSerial(ReadOnlySpan<byte> serial)
    {
        string hex = new BigInteger(serial, isUnsigned: true, isBigEndian: true).ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
        return hex.Length == 0 ? "0" : hex;
    }

    private static string ReadAlgorithmIdentifier(ReadOnlyMemory<byte> encoded, out ReadOnlyMemory<byte>? parameters, bool subjectPublicKeyInfo = false)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.BER);
        AsnReader sequence = reader.ReadSequence();
        reader.ThrowIfNotEmpty();

        if (subjectPublicKeyInfo)
        {
            sequence = sequence.ReadSequence();
        }

        string oid = sequence.ReadObjectIdentifier();
        parameters = sequence.HasData ? sequence.ReadEncodedValue() : (ReadOnlyMemory<byte>?)null;
        sequence.ThrowIfNotEmpty();
        return oid;
    }

    /// <summary>Re-encodes the key as DER, so pinned keys and trust anchors compare by value.</summary>
    private static byte[] NormalizeSubjectPublicKeyInfo(ReadOnlyMemory<byte> encoded)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.BER);
        AsnReader sequence = reader.ReadSequence();
        reader.ThrowIfNotEmpty();
        string algorithm = ReadAlgorithmIdentifier(sequence.ReadEncodedValue(), out ReadOnlyMemory<byte>? parameters);
        byte[] key = sequence.ReadBitString(out int unusedBits);
        sequence.ThrowIfNotEmpty();

        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(algorithm);

                if (parameters is { } value)
                {
                    writer.WriteEncodedValue(value.Span);
                }
            }

            writer.WriteBitString(key, unusedBits);
        }

        return writer.Encode();
    }

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    /// <summary>An X.501 name: relative distinguished names, each a set of attributes.</summary>
    private sealed class Name : List<List<(string Type, ReadOnlyMemory<byte> Value)>>;

    private static Name ReadName(AsnReader reader)
    {
        var name = new Name();
        AsnReader sequence = reader.ReadSequence();

        while (sequence.HasData)
        {
            AsnReader set = sequence.ReadSetOf(skipSortOrderValidation: true);
            var rdn = new List<(string, ReadOnlyMemory<byte>)>();

            while (set.HasData)
            {
                AsnReader attribute = set.ReadSequence();
                rdn.Add((attribute.ReadObjectIdentifier(), attribute.ReadEncodedValue()));
                attribute.ThrowIfNotEmpty();
            }

            name.Add(rdn);
        }

        return name;
    }

    /// <summary>Compares names in order, with RFC 5280's case-insensitive, whitespace-insensitive string matching.</summary>
    private static bool NamesMatch(Name left, Name right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            List<(string Type, ReadOnlyMemory<byte> Value)> leftRdn = left[i];
            List<(string Type, ReadOnlyMemory<byte> Value)> rightRdn = right[i];

            if (leftRdn.Count != rightRdn.Count)
            {
                return false;
            }

            for (int j = 0; j < leftRdn.Count; j++)
            {
                (string? leftType, ReadOnlyMemory<byte> leftValue) = leftRdn[j];
                (string? rightType, ReadOnlyMemory<byte> rightValue) = rightRdn[j];

                if (leftType != rightType || !ValuesMatch(leftValue, rightValue))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValuesMatch(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
    {
        if (left.Span.SequenceEqual(right.Span))
        {
            return true;
        }

        return TryReadString(left, out string? leftString) &&
            TryReadString(right, out string? rightString) &&
            string.Equals(NormalizeString(leftString), NormalizeString(rightString), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadString(ReadOnlyMemory<byte> encoded, [NotNullWhen(true)] out string? value)
    {
        value = null;

        try
        {
            var reader = new AsnReader(encoded, AsnEncodingRules.BER);
            Asn1Tag tag = reader.PeekTag();

            if (tag.TagClass != TagClass.Universal || tag.TagValue is not ((int)UniversalTagNumber.UTF8String or (int)UniversalTagNumber.PrintableString or (int)UniversalTagNumber.IA5String or (int)UniversalTagNumber.T61String or (int)UniversalTagNumber.BMPString or (int)UniversalTagNumber.UniversalString))
            {
                return false;
            }

            value = reader.ReadCharacterString((UniversalTagNumber)tag.TagValue);
            reader.ThrowIfNotEmpty();
            return true;
        }
        catch (Exception ex) when (ex is AsnContentException or ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeString(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
