using MihuBot.Molly.Attestation;

#nullable enable

namespace MihuBot.Molly;

/// <param name="ProtectedId">
/// The opaque token identifying the entry. The real database id is never sent to a client.
/// </param>
/// <param name="AttestationFailure">Why attestation failed, if it did. Never sent to the client.</param>
public sealed record MollyLoginResult(MollyResultStatus Status, string? ProtectedId, byte[]? ServerHmac, MollyCommand Command, MollyAttestationFailure AttestationFailure = MollyAttestationFailure.None)
{
    public static MollyLoginResult Invalid { get; } = new(MollyResultStatus.InvalidRequest, null, null, MollyCommand.None);

    /// <summary>An unknown key hash without anything authorizing its registration.</summary>
    public static MollyLoginResult Unregistered { get; } = new(MollyResultStatus.Unregistered, null, null, MollyCommand.None);

    /// <summary>
    /// A login of a locked entry. It's answered exactly like <see cref="Unregistered"/> (a wrong passphrase), as only the right
    /// passphrase gets this far: telling them apart would confirm it to whoever is typing it.
    /// </summary>
    public static MollyLoginResult Locked(MollyAttestationFailure attestationFailure = MollyAttestationFailure.None) => new(MollyResultStatus.Unregistered, null, null, MollyCommand.None, attestationFailure);

    public static MollyLoginResult Success(string protectedId, byte[] serverHmac, MollyCommand command = MollyCommand.None) =>
        new(MollyResultStatus.Ok, protectedId, serverHmac, command);

    /// <summary>The device gets the command instead of the secret, so nothing else is returned.</summary>
    public static MollyLoginResult Blocked(MollyCommand command, MollyAttestationFailure attestationFailure = MollyAttestationFailure.None) => new(MollyResultStatus.Command, null, null, command, attestationFailure);

    /// <summary>The login wasn't attested, so nothing is released. The client only sees an invalid request.</summary>
    public static MollyLoginResult Failed(MollyAttestationFailure failure) => new(MollyResultStatus.InvalidRequest, null, null, MollyCommand.None, failure);
}
