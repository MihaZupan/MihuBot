using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyPingRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Optional battery percentage, 0-100.</summary>
    [JsonPropertyName("batteryLevel")]
    public int? BatteryLevel { get; set; }

    /// <summary>Optional: whether the device can currently get a location fix.</summary>
    [JsonPropertyName("locationEnabled")]
    public bool? LocationEnabled { get; set; }

    /// <summary>Optional current app version: up to 32 ASCII letters, digits, or ._-. Null preserves the last reported version.</summary>
    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }
}
