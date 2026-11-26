using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyLoginRequest
{
    [JsonPropertyName("keyHash")]
    public string? KeyHash { get; set; }

    /// <summary>Required for the server secret to be released. See Attestation.md.</summary>
    [JsonPropertyName("attestation")]
    public MollyAttestationProof? Attestation { get; set; }

    /// <summary>
    /// Authorizes registering a new entry if the key hash is unknown: a token from a setup code (Provisioning.md).
    /// Ignored for known key hashes.
    /// </summary>
    [JsonPropertyName("registrationToken")]
    public string? RegistrationToken { get; set; }

    /// <summary>
    /// The device's current session, which moves its entry to an unknown key hash instead of registering a new one
    /// (a passphrase change, Attestation.md). Takes precedence over <see cref="RegistrationToken"/>. Ignored for known key hashes.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
