using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Alerts;

/// <summary>
/// The envelope every alert is wrapped in. <see cref="Data"/> is left as raw JSON so that an
/// unrecognized <see cref="Type"/> doesn't stop the alert from being stored.
/// </summary>
public sealed class MollyAlertEnvelope
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Kept as a string so an unknown type parses as <see cref="MollyAlertType.Unknown"/>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    public MollyAlertType AlertType =>
        Enum.TryParse(Type, ignoreCase: true, out MollyAlertType type) && Enum.IsDefined(type)
            ? type
            : MollyAlertType.Unknown;

    /// <summary>Deserializes <see cref="Data"/>, or null if it isn't the expected shape.</summary>
    public T? TryGetData<T>() where T : class
    {
        if (Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return Data.Deserialize<T>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses an alert body, or returns null if it isn't a JSON object.</summary>
    public static MollyAlertEnvelope? TryParse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<MollyAlertEnvelope>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
