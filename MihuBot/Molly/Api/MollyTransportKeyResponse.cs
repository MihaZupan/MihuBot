using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyTransportKeyResponse
{
    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; init; }

    [JsonPropertyName("expiresAt")]
    public required long ExpiresAt { get; init; }
}
