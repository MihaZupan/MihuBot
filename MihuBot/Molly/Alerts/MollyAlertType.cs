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

    /// <summary>The device confirming a lock/wipe command right before it carries it out.</summary>
    CommandAck,
}
