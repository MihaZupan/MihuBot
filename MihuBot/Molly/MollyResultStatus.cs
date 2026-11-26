#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// The outcome of a Molly API operation, reported in the encrypted response body.
/// HTTP status codes only describe the transport.
/// </summary>
public enum MollyResultStatus
{
    /// <summary>The request succeeded.</summary>
    Ok,
    /// <summary>The request payload was malformed, or the entry it referenced no longer exists.</summary>
    InvalidRequest,
    /// <summary>The entry exists, but the device has to run <see cref="MollyLoginResult.Command"/> instead.</summary>
    Command,
    /// <summary>
    /// Login only: the key hash isn't known and the request carried nothing that authorizes registering it
    /// (no valid, unused registration token nor current session). Nothing was attested or changed.
    /// </summary>
    Unregistered,
}

public static class MollyResultStatusExtensions
{
    /// <summary>The value sent to the client in the response envelope.</summary>
    public static string ToWireValue(this MollyResultStatus status) => status switch
    {
        MollyResultStatus.Ok => "ok",
        MollyResultStatus.Command => "command",
        MollyResultStatus.Unregistered => "unregistered",
        _ => "invalid",
    };
}
