using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>The <c>attestation</c> object of a login request. See Attestation.md.</summary>
public sealed class MollyAttestationProof
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary><c>strict</c> or <c>fallback</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>Leaf-first base64 DER certificates of the fresh per-login key.</summary>
    [JsonPropertyName("freshChain")]
    public string[]? FreshChain { get; set; }

    /// <summary>Leaf-first base64 DER certificates of the persistent signing key. Required to enroll; always sent.</summary>
    [JsonPropertyName("signingKeyChain")]
    public string[]? SigningKeyChain { get; set; }

    /// <summary>Leaf-first base64 DER certificates of the persistent attest key, in strict mode. Required to enroll; always sent.</summary>
    [JsonPropertyName("attestKeyChain")]
    public string[]? AttestKeyChain { get; set; }

    /// <summary>Base64 DER ECDSA signature over the transcript, by the persistent signing key.</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}
