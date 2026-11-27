using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

public sealed class MollyDashboardLoginRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
