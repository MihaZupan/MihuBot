#nullable enable

namespace MihuBot.Molly;

public static class MollyCommandExtensions
{
    /// <summary>
    /// Whether the command replaces the normal response payload. Non-blocking commands are
    /// delivered alongside it, so adding an informational command doesn't break existing flows.
    /// </summary>
    public static bool IsBlocking(this MollyCommand command) => (command & MollyCommand.Blocking) != 0;

    /// <summary>The value sent to the client, or null when there is nothing to report.</summary>
    public static string? ToWireValue(this MollyCommand command) => command switch
    {
        MollyCommand.Lock => "lock",
        MollyCommand.Wipe => "wipe",
        _ => null,
    };
}
