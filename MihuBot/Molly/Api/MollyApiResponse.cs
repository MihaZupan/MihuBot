using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>
/// The JSON data of every Molly API response, after decryption and padding removal.
/// <see cref="Status"/> carries the operation's outcome. There is no nonce echo: each request derives a
/// unique session key, so a response only decrypts for the exact request it answers.
/// </summary>
public sealed class MollyApiResponse
{
    /// <summary>The <see cref="MollyResultStatus"/> wire value.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>The action specific payload, omitted when there is nothing to report.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}
