using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>
/// Either the secret and its id, or the command the device has to run instead. Never both.
/// </summary>
public sealed class MollyLoginResponse
{
    [JsonPropertyName("serverHmac")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerHmac { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; set; }
}
