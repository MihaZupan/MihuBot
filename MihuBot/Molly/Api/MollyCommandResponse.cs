using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyCommandResponse
{
    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; set; }
}
