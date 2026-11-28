using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Alerts;

/// <summary>
/// The <c>data</c> of a <see cref="MollyAlertType.Integrity"/> alert - a device integrity check
/// triggered on the phone, optionally asking the server to also enforce a server lock.
/// </summary>
public sealed class MollyIntegrityAlert
{
    /// <summary>A short identifier for the check group, e.g. <c>adb</c>, <c>usb</c>, or <c>sim</c>.</summary>
    [JsonPropertyName("check")]
    public string? Check { get; set; }

    /// <summary>Optional human-readable detail accompanying the check.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>Whether the device is asking the server to enforce a server lock on this entry.</summary>
    [JsonPropertyName("requestServerLock")]
    public bool RequestServerLock { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Check);

    /// <summary>A one-line description for the dashboard, or null if there's no usable check name.</summary>
    public string? Summary
    {
        get
        {
            if (!IsValid)
            {
                return null;
            }

            string check = Check!.Trim();
            string? detail = string.IsNullOrWhiteSpace(Detail) ? null : Detail.Trim();
            string summary = detail is null ? check : $"{check} - {detail}";

            return RequestServerLock ? $"{summary} (lock requested)" : summary;
        }
    }
}
