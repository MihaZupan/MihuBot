using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyAssociateRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }
}
