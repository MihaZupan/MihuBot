#nullable enable

namespace MihuBot.Molly.Alerts;

/// <summary>
/// The payload shapes the server knows how to interpret. Anything else is still stored and shown
/// on the dashboard, it just isn't acted on.
/// </summary>
public enum MollyAlertType
{
    Unknown = 0,
    Location,

    /// <summary>A general status report from the device, e.g. that it locked, wiped, or came online.</summary>
    Status,
}
