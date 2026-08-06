#nullable enable

namespace MihuBot.Molly;

/// <param name="ProtectedId">
/// The opaque token identifying the entry. The real database id is never sent to a client.
/// </param>
public sealed record MollyLoginResult(MollyResultStatus Status, string? ProtectedId, byte[]? ServerHmac, MollyCommand Command)
{
    public static MollyLoginResult Invalid { get; } = new(MollyResultStatus.InvalidRequest, null, null, MollyCommand.None);

    public static MollyLoginResult Success(string protectedId, byte[] serverHmac, MollyCommand command = MollyCommand.None) =>
        new(MollyResultStatus.Ok, protectedId, serverHmac, command);

    /// <summary>The device gets the command instead of the secret, so nothing else is returned.</summary>
    public static MollyLoginResult Blocked(MollyCommand command) => new(MollyResultStatus.Command, null, null, command);
}
