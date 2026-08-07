using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>
/// The decrypted body of every Molly API response. HTTP status codes no longer carry the outcome
/// of an operation - <see cref="Status"/> does. There is no nonce echo: each request derives a
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
