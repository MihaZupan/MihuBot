#nullable enable

namespace MihuBot.Molly.Attestation;

/// <summary>
/// Trust anchors for Android hardware key attestation, identified by their DER SubjectPublicKeyInfo.
/// Pinning the key rather than a root certificate covers every re-issued root certificate for the same key.
/// </summary>
/// <remarks>
/// Sourced from https://android.googleapis.com/attestation/root, as documented at
/// https://developer.android.com/privacy-and-security/security-key-attestation#root_certificate.
/// New anchors are added by updating this list - never from configuration, the network, or the client.
/// </remarks>
public sealed class MollyAttestationTrustAnchors
{
    /// <summary>Google Hardware Attestation Root (RSA 4096). Shared by the 2016, 2019, 2021 and 2022 root certificates.</summary>
    private const string GoogleRsaRoot =
        "MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAr7bHgiuxpwHsK7Qui8xUFmOr75gvMsd/dTEDDJdSSxtf6An7xyqpRR90PL2abxM1dEqlXnf2tqw1Ne4Xwl5jlRfdnJLmN0pTy/4lj4/7tv0Sk3iiKkypnEUtR6WfMgH0QZfKHM1+di+y9TFRtv6y//0rb+T+W8a9nsNL/ggjnar86461qO0rOs2cXjp3kOG1FEJ5MVmFmBGtnrKpa73XpXyTqRxB/M0n1n/W9nGqC4FSYa04T6N5RIZGBN2z2MT5IKGbFlbC8UrW0DxW7AYImQQcHtGl/m00QLVWutHQoVJYnFPlXTcHYvASLu+RhhsbDmxMgJJ0mcDpvsC4PjvB+TxywElgS70vE0XmLD+OJtvsBslHZvPBKCOdT0MS+tgSOIfga+z1Z1g7+DVagf7quvmag8jfPioyKvxnK/EgsTUVi2ghzq8wm27ud/mIM7AY2qEORR8Go3TVB4HzWQgpZrt3i5MIlCaY504LzSRiigHCzAPlHws+W0rB5N+er5/2pJKnfBSDiCiFAVtCLOZ7gLiMm0jhO2B6tUXHI/+MRPjy02i59lINMRRev56GKtcd9qO/0kUJWdZTdA2XoS82ixPvZtXQpUpuL12ab+9EaDK8Z4RHJYYfCT3Q5vNAXaiWQ+8PTWm2QgBR/bkwSWc+NpUFgNPN9PvQi8WEg5UmAGMCAwEAAQ==";

    /// <summary>Key Attestation CA1 (ECDSA P-384), the 2025 root generation.</summary>
    private const string GoogleEcdsaRoot =
        "MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAEI9ojcU7fPlsFCjxy6IRqzgeOoK0b+YsV9FPQywiyw8EQRTkJ9u3qwfnI4DGoSLlBqClTXJfgfCcZvs60FikNMHnu4fkRzObfgDkU2KNXezT9/RQ+XvNslxPHrHCowhGr";

    private readonly byte[][] _factoryRoots;

    public MollyAttestationTrustAnchors() : this([Convert.FromBase64String(GoogleRsaRoot), Convert.FromBase64String(GoogleEcdsaRoot)], [Convert.FromBase64String(GoogleRsaRoot)])
    { }

    /// <summary>Exists so tests can use their own roots. Production uses the parameterless constructor.</summary>
    internal MollyAttestationTrustAnchors(IEnumerable<byte[]> subjectPublicKeyInfos, IEnumerable<byte[]>? factoryRoots = null)
    {
        SubjectPublicKeyInfos = [.. subjectPublicKeyInfos.Select(k => (byte[])k.Clone())];
        _factoryRoots = [.. (factoryRoots ?? []).Select(k => (byte[])k.Clone())];

        if (SubjectPublicKeyInfos.Length == 0 || _factoryRoots.Any(root => !IsTrusted(root)))
        {
            throw new ArgumentException("At least one trust anchor is required, and factory roots must be trust anchors.", nameof(subjectPublicKeyInfos));
        }
    }

    public byte[][] SubjectPublicKeyInfos { get; }

    /// <summary>
    /// Whether factory-provisioned chains under this root stay trusted after their certificates expire.
    /// Google: devices launched before 2021 have expired factory certificates that are "still trustworthy
    /// unless they appear in the certificate revocation list", for chains to the RSA root. RKP certificates
    /// must still have their validity checked.
    /// </summary>
    public bool IsFactoryRoot(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        foreach (byte[] root in _factoryRoots)
        {
            if (subjectPublicKeyInfo.SequenceEqual(root))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsTrusted(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        foreach (byte[] anchor in SubjectPublicKeyInfos)
        {
            if (subjectPublicKeyInfo.SequenceEqual(anchor))
            {
                return true;
            }
        }

        return false;
    }
}
