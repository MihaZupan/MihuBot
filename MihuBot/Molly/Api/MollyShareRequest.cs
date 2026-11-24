using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>The <c>data</c> of every file sharing action. Byte fields are standard padded base64.</summary>
public sealed class MollyShareRequest
{
    /// <summary>The device session token. The caller is the Signal identity currently bound to its entry.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>The action's binary body, <c>""</c> when empty.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }
}
