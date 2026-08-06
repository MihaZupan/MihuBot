using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyLoginRequest
{
    [JsonPropertyName("keyHash")]
    public string? KeyHash { get; set; }
}
