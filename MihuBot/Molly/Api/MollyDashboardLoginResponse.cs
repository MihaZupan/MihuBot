using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyDashboardLoginResponse : MollyCommandResponse
{
    /// <summary>The dashboard URL, with a <c>login</c> query only if an admin allowed dashboard login for the device.</summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }
}
