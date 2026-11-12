using Microsoft.Extensions.Logging;
using MihuBot.Configuration;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly.Attestation;

/// <summary>
/// Ties the attestation pieces together for <see cref="MollyService"/>: request decoding, freshness,
/// trust data availability and the verifier.
/// </summary>
public sealed class MollyAttestationService
{
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The oldest transport key an attested login may be sealed to. The attestation challenge is derived
    /// from the exchange, so this bounds how long ago the per-login key can have been attested.
    /// </summary>
    public static readonly TimeSpan MaxTransportKeyAge = TimeSpan.FromMinutes(2);

    private readonly MollyAttestationVerifier _verifier;
    private readonly MollyAttestationRevocations _revocations;
    private readonly ILogger<MollyAttestationService> _logger;

    public MollyAttestationService(IConfiguration configuration, MollyAttestationTrustAnchors anchors, MollyAttestationRevocations revocations, ILogger<MollyAttestationService> logger)
        : this(ParseApps(configuration[OptionalFeatures.MollyAttestationAppsName]), anchors, revocations, logger, TimeProvider.System)
    { }

    internal MollyAttestationService(IEnumerable<MollyAttestationApp> apps, MollyAttestationTrustAnchors anchors, MollyAttestationRevocations revocations, ILogger<MollyAttestationService> logger, TimeProvider time)
    {
        MollyAttestationApp[] allowedApps = [.. apps];

        _verifier = new MollyAttestationVerifier(anchors, allowedApps, time);
        _revocations = revocations;
        _logger = logger;

        if (allowedApps.Length == 0)
        {
            _logger.LogWarning("{Key} isn't configured, so Molly logins from any well-formed app identity can enroll", OptionalFeatures.MollyAttestationAppsName);
        }
    }

    public MollyRevocationStatus RevocationStatus => _revocations.GetStatus(out _);

    /// <summary>Decodes the proof and checks that the exchange it arrived in is fresh enough to attest in.</summary>
    public MollyAttestationFailure TryDecode(MollyAttestationProof? proof, MollyExchangeContext exchange, out MollyAttestationEvidence? evidence)
    {
        evidence = null;

        if (proof is null)
        {
            return MollyAttestationFailure.MissingProof;
        }

        if (proof.Version != ProtocolVersion ||
            !MollyAttestationModeExtensions.TryParse(proof.Mode, out MollyAttestationMode mode) ||
            !MollyService.TryDecodeBase64(proof.Signature, 1, MollyAttestationVerifier.MaxSignatureLength, out byte[] signature) ||
            !TryDecodeChain(proof.FreshChain, required: true, out byte[][] freshChain) ||
            !TryDecodeChain(proof.SigningKeyChain, required: false, out byte[][] signingKeyChain) ||
            !TryDecodeChain(proof.AttestKeyChain, required: false, out byte[][] attestKeyChain))
        {
            return MollyAttestationFailure.MalformedProof;
        }

        if (exchange.RecipientKeyAge > MaxTransportKeyAge)
        {
            return MollyAttestationFailure.TransportKeyTooOld;
        }

        evidence = new MollyAttestationEvidence(mode, freshChain, signingKeyChain, attestKeyChain, signature);
        return MollyAttestationFailure.None;
    }

    /// <summary>
    /// Verifies a first enrollment (<paramref name="pinned"/> is null) or a login against pinned state.
    /// </summary>
    /// <remarks>
    /// Without a revocation list nothing is verified. A stale list only still allows logins of enrolled
    /// strict devices - their evidence is tied to the pinned hardware key rather than to the manufacturer
    /// chain the list would vouch for. Known revocations are always enforced.
    /// </remarks>
    public MollyAttestationResult Verify(MollyAttestationEvidence evidence, byte[] keyHash, MollyExchangeContext exchange, MollyAttestationState? pinned)
    {
        MollyRevocationStatus status = _revocations.GetStatus(out MollyRevocationSnapshot? revocations);

        if (status == MollyRevocationStatus.Unavailable)
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.RevocationDataUnavailable);
        }

        if (status == MollyRevocationStatus.Stale && (pinned is null || pinned.Mode != MollyAttestationMode.Strict))
        {
            return MollyAttestationResult.Fail(MollyAttestationFailure.RevocationDataStale);
        }

        return pinned is null
            ? _verifier.VerifyEnrollment(evidence, keyHash, exchange, revocations!)
            : _verifier.VerifyLogin(evidence, keyHash, exchange, pinned, revocations!);
    }

    private static bool TryDecodeChain(string[]? encoded, bool required, out byte[][] chain)
    {
        chain = [];

        if (encoded is null)
        {
            return !required;
        }

        if (encoded.Length > MollyAttestationVerifier.MaxChainLength || (required && encoded.Length == 0))
        {
            return false;
        }

        var decoded = new byte[encoded.Length][];

        for (int i = 0; i < encoded.Length; i++)
        {
            if (!MollyService.TryDecodeBase64(encoded[i], 1, MollyAttestationCertificate.MaxEncodedLength, out decoded[i]))
            {
                return false;
            }
        }

        chain = decoded;
        return true;
    }

    /// <summary>
    /// <c>package:sha256hex</c> entries separated by commas. The digest is SHA-256 of the app's signing
    /// certificate, as reported in <c>AttestationApplicationId</c>. Hex may be colon-separated.
    /// </summary>
    internal static MollyAttestationApp[] ParseApps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(entry =>
        {
            int separator = entry.IndexOf(':', StringComparison.Ordinal);

            if (separator <= 0)
            {
                throw new FormatException($"{OptionalFeatures.MollyAttestationAppsName} entries must be 'package:sha256hex'.");
            }

            string packageName = entry.Substring(0, separator).Trim();
            byte[] digest = Convert.FromHexString(entry.Substring(separator + 1).Replace(":", "", StringComparison.Ordinal).Trim());

            if (!MollyAttestationVerifier.IsValidPackageName(packageName))
            {
                throw new FormatException($"{OptionalFeatures.MollyAttestationAppsName} contains an invalid package name '{packageName}'.");
            }

            if (digest.Length != 32)
            {
                throw new FormatException($"{OptionalFeatures.MollyAttestationAppsName} digests must be SHA-256.");
            }

            return new MollyAttestationApp(packageName, digest);
        })];
    }
}
