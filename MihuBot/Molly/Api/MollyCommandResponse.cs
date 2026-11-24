using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>The fields any session-bound response may carry, alongside its action-specific ones.</summary>
public class MollyCommandResponse
{
    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; set; }

    /// <summary>
    /// The file sharing update hint (Unix milliseconds) of the entry's current identity, omitted while unbound and
    /// for actions that don't report it. Clients sync when it exceeds the <c>updatedAt</c> of their last sync.
    /// </summary>
    [JsonPropertyName("shareUpdatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ShareUpdatedAt { get; set; }
}