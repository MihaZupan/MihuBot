using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>
/// The decrypted body of every Molly API call. The endpoint is a single URL, so the operation is
/// part of the payload instead of the path.
/// </summary>
public sealed class MollyApiRequest
{
    /// <summary>Which operation to run, see <see cref="MollyApiActions"/>.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>Unix seconds. Must be within 30 seconds of the server's clock.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>Base64 of 16 random bytes, rejected if reused (server-side replay protection).</summary>
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    /// <summary>The action specific payload. <see cref="JsonValueKind.Undefined"/> when omitted.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}
