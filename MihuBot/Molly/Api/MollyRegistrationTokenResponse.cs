using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>Both fields are omitted when the device hit a token issuance limit.</summary>
public sealed class MollyRegistrationTokenResponse : MollyCommandResponse
{
    /// <summary>The registration token (unpadded base64url) for a setup code.</summary>
    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }

    /// <summary>Unix seconds after which the token can no longer be redeemed.</summary>
    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpiresAt { get; set; }
}
