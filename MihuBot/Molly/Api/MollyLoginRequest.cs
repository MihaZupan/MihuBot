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
}
