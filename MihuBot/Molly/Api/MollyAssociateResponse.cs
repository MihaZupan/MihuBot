using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyAssociateResponse : MollyCommandResponse
{
    /// <summary><c>bound</c> or <c>cooldown</c>; set when the status is <c>ok</c>.</summary>
    [JsonPropertyName("identity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Identity { get; set; }
}