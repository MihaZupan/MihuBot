using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

public sealed class MollyCommandTests
{
    [Theory]
    [InlineData(MollyCommand.Lock)]
    [InlineData(MollyCommand.Wipe)]
    public void LockAndWipe_AreBlocking(MollyCommand command)
    {
        Assert.True(command.IsBlocking());
    }

    [Fact]
    public void None_IsNotBlocking()
    {
        Assert.False(MollyCommand.None.IsBlocking());
    }

    [Fact]
    public void ACommandWithoutTheBlockingFlag_IsNotBlocking()
    {
        // Stands in for a future informational command that accompanies a normal response.
        const MollyCommand Informational = (MollyCommand)(1 << 3);

        Assert.False(Informational.IsBlocking());
        Assert.Null(Informational.ToWireValue());
    }

    [Fact]
    public void BlockingCommands_AreDistinguishableFromEachOther()
    {
        // The shared Blocking bit must not make one command look like another.
        Assert.False(MollyCommand.Wipe.HasFlag(MollyCommand.Lock));
        Assert.False(MollyCommand.Lock.HasFlag(MollyCommand.Wipe));
        Assert.NotEqual(MollyCommand.Lock, MollyCommand.Wipe);
    }

    [Fact]
    public void BlockingFlag_IsNotAValueOnItsOwn()
    {
        // Blocking is a modifier, so it has no wire value of its own.
        Assert.Null(MollyCommand.Blocking.ToWireValue());
        Assert.True(MollyCommand.Blocking.IsBlocking());
    }

    [Theory]
    [InlineData(MollyCommand.Lock, "lock")]
    [InlineData(MollyCommand.Wipe, "wipe")]
    [InlineData(MollyCommand.None, null)]
    public void ToWireValue_MapsKnownCommands(MollyCommand command, string? expected)
    {
        Assert.Equal(expected, command.ToWireValue());
    }

    [Fact]
    public void SuccessfulResults_CanCarryANonBlockingCommand()
    {
        const MollyCommand Informational = (MollyCommand)(1 << 3);

        MollyLoginResult login = MollyLoginResult.Success("token", new byte[64], Informational);
        Assert.Equal(MollyResultStatus.Ok, login.Status);
        Assert.Equal(Informational, login.Command);
        Assert.NotNull(login.ServerHmac);

        MollyCommandResult command = MollyCommandResult.Ok(Informational);
        Assert.Equal(MollyResultStatus.Ok, command.Status);
        Assert.Equal(Informational, command.Command);
    }

    [Fact]
    public void BlockedResults_WithholdThePayload()
    {
        MollyLoginResult login = MollyLoginResult.Blocked(MollyCommand.Wipe);

        Assert.Equal(MollyResultStatus.Command, login.Status);
        Assert.Null(login.ServerHmac);
        Assert.Null(login.ProtectedId);
    }

    [Fact]
    public void ResultsDefaultToNoCommand()
    {
        Assert.Equal(MollyCommand.None, MollyLoginResult.Success("token", new byte[64]).Command);
        Assert.Equal(MollyCommand.None, MollyCommandResult.Ok().Command);
    }
}
