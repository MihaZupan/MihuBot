#nullable enable

namespace MihuBot.Molly;

public sealed record MollyCommandResult(MollyResultStatus Status, MollyCommand Command)
{
    public static MollyCommandResult Invalid { get; } = new(MollyResultStatus.InvalidRequest, MollyCommand.None);

    public static MollyCommandResult Ok(MollyCommand command = MollyCommand.None) => new(MollyResultStatus.Ok, command);

    /// <summary>The device gets the command instead of the normal response.</summary>
    public static MollyCommandResult Blocked(MollyCommand command) => new(MollyResultStatus.Command, command);

    /// <summary>
    /// For successful pings of entries with a current identity, that identity's file sharing update hint in Unix
    /// milliseconds (0 if nothing affected it yet).
    /// </summary>
    public long? ShareUpdatedAt { get; init; }

    /// <summary>For successful associates that carried an identity, the outcome of binding it.</summary>
    public MollyIdentityBinding? Identity { get; init; }

    /// <summary>For authorized file sharing requests, the entry the request acts as.</summary>
    internal Guid EntryId { get; init; }

    /// <summary>For authorized file sharing requests, the party hash of the entry's current identity; null while unbound.</summary>
    internal byte[]? PartyHash { get; init; }
}
