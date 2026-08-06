#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// A remote action the device has to carry out. Commands are flags so that
/// <see cref="Blocking"/> can mark the ones that replace the normal response.
/// </summary>
[Flags]
public enum MollyCommand
{
    None = 0,

    /// <summary>
    /// Marks a command as taking the place of the response payload rather than accompanying it.
    /// Informational commands leave this unset and are delivered alongside a normal response.
    /// </summary>
    Blocking = 1 << 0,

    /// <summary>Logins are refused until an admin unlocks the entry.</summary>
    Lock = Blocking | (1 << 1),

    /// <summary>The device has to destroy its local data.</summary>
    Wipe = Blocking | (1 << 2),
}
