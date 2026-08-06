#nullable enable

namespace MihuBot.Molly;

public sealed record MollyCommandResult(MollyResultStatus Status, MollyCommand Command)
{
    public static MollyCommandResult Invalid { get; } = new(MollyResultStatus.InvalidRequest, MollyCommand.None);

    public static MollyCommandResult Ok(MollyCommand command = MollyCommand.None) => new(MollyResultStatus.Ok, command);

    /// <summary>The device gets the command instead of the normal response.</summary>
    public static MollyCommandResult Blocked(MollyCommand command) => new(MollyResultStatus.Command, command);
}
