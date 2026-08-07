using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Alerts;

/// <summary>
/// The <c>data</c> of a <see cref="MollyAlertType.Status"/> alert - a general status report from the
/// device. It replaces the old command-acknowledgement alert with something free-form, so the app can
/// report any status (that it locked or wiped itself, came back online, hit a low battery, and so on)
/// without needing a server change per case.
/// </summary>
public sealed class MollyStatusAlert
{
    /// <summary>A short status token, e.g. <c>locked</c>, <c>wiped</c>, or <c>online</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Optional human-readable detail accompanying the status.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Status);

    /// <summary>A one-line description for the dashboard, or null if there's no usable status.</summary>
    public string? Summary
    {
        get
        {
            if (!IsValid)
            {
                return null;
            }

            string status = Status!.Trim();
            string? detail = string.IsNullOrWhiteSpace(Detail) ? null : Detail.Trim();

            return detail is null ? status : $"{status} - {detail}";
        }
    }
}
