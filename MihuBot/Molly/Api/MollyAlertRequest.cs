using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>
/// Only the id is interpreted - the rest of the payload is stored as sent, so the app can
/// upload whatever it needs to without a server change.
/// </summary>
public sealed class MollyAlertRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
