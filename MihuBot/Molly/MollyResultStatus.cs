#nullable enable

namespace MihuBot.Molly;

/// <summary>The outcome of a Molly API operation, mapped to a status code by the endpoints.</summary>
public enum MollyResultStatus
{
    /// <summary>The request succeeded.</summary>
    Ok,
    /// <summary>The request payload was malformed, or the entry it referenced no longer exists.</summary>
    InvalidRequest,
    /// <summary>The entry exists, but the device has to run <see cref="MollyLoginResult.Command"/> instead.</summary>
    Command,
}
