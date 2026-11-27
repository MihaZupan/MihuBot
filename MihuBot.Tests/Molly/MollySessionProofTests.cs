using System.Security.Cryptography;
using MihuBot.Molly;
using MihuBot.Molly.Api;

namespace MihuBot.Tests.Molly;

public sealed class MollySessionProofTests(MollyServiceFixture fixture) : IClassFixture<MollyServiceFixture>
{
    private MollyService Molly => fixture.Service;

    [Fact]
    public void Proof_MatchesTheDocumentedVector()
    {
        byte[] serverHmac = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var exchange = new MollyExchangeContext(new MollyPublicKeyId(Enumerable.Repeat((byte)0x11, 16).ToArray()), Enumerable.Repeat((byte)0x22, 32).ToArray(), Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), 1767225600, TimeSpan.Zero);

        byte[] key = new byte[MollySessionProof.KeyLength];
        MollySessionProof.DeriveKey(serverHmac, "session-id", key);
        Assert.Equal(DocumentedKey, Convert.ToHexString(key));
        Assert.Equal(DocumentedProof, MollySessionProof.Compute(serverHmac, "session-id", exchange with { Action = "dashboard-login" }));
    }

    private const string DocumentedKey = "CE9E37D5AE1556B59BB815FD476F2254A2D8610ABBDF2EDB1258F969925E71591692F92A1921DACDB26A52AE2805F3EAD9F197AE8FCEB6D3EE61E6C2E3B41BCF";

    private const string DocumentedProof = "SKajUvGvn2S0oMHMhXE9hF/p9pyRAwnSWYPsfZFIoucgnAAc66zggdQTSjDeH57naEia8vqSEzivmtA9ByDhQw==";

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("other-session")]
    [InlineData("other-action")]
    [InlineData("other-exchange")]
    [InlineData("other-timestamp")]
    public async Task ActionsOfTheUnlockedApp_RejectRequestsWithoutAValidProof_AndChangeNothing(string failure)
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult first = await fixture.LoginAsync(keyHash);
        await Molly.AssociateAsync(first.ProtectedId, "device");
        MollyLoginResult second = await fixture.LoginAsync(keyHash);
        Guid id = fixture.Unprotect(second.ProtectedId);
        string session = second.ProtectedId;
        await fixture.SetLastSeenAsync(id, DateTime.UtcNow.AddDays(-1));
        MollyDbEntry before = await fixture.GetEntryAsync(id);

        MollyExchangeContext Exchange(string action)
        {
            MollyExchangeContext exchange = MollyTestSessions.ProvenExchange(session, action);

            return failure switch
            {
                "missing" => exchange with { SessionProof = null },
                "malformed" => exchange with { SessionProof = Convert.ToBase64String(new byte[HMACSHA512.HashSizeInBytes - 1]) },
                "other-session" => MollyTestSessions.ProvenExchange(first.ProtectedId, action, exchange),
                "other-action" => MollyTestSessions.ProvenExchange(session, MollyApiActions.Ping, exchange) with { Action = action },
                "other-timestamp" => exchange with { RequestTimestamp = exchange.RequestTimestamp - 1 },
                _ => exchange with { RequestNonce = MollyTestDevice.NewExchange().RequestNonce },
            };
        }

        Assert.Equal(MollyCommandResult.Invalid, await Molly.IssueDashboardLoginAsync(new MollyDashboardLoginRequest { Id = session }, Exchange(MollyApiActions.DashboardLogin), default));
        Assert.Equal(MollyCommandResult.Invalid, await Molly.IssueRegistrationTokenAsync(session, Exchange(MollyApiActions.RegistrationToken), default));
        Assert.Equal(MollyCommandResult.Invalid, await Molly.AssociateAsync(session, "renamed", MollyServiceTestExtensions.TestIdentityPartyHash, Exchange(MollyApiActions.Associate), default));
        Assert.Equal(MollyCommandResult.Invalid, await Molly.AuthorizeShareRequestAsync(session, Exchange(MollyApiActions.ShareSync), default));

        // Not even recorded as a check-in, nor does it supersede the previous session.
        MollyDbEntry after = await fixture.GetEntryAsync(id);
        Assert.Equal(before.LastSeenAt, after.LastSeenAt);
        Assert.Equal(before.ActiveSessionIssuedAt, after.ActiveSessionIssuedAt);
        Assert.Equal(before.EncryptedNickname, after.EncryptedNickname);
        Assert.Equal(MollyResultStatus.Ok, (await Molly.PingAsync(first.ProtectedId, null, null, null, default)).Status);

        Assert.Equal(MollyResultStatus.Ok, (await Molly.IssueDashboardLoginAsync(session)).Status);
    }

    [Fact]
    public async Task AWipedEntry_IsAnsweredWithTheWipeWithoutAProof()
    {
        MollyLoginResult login = await fixture.LoginAsync(MollyTestKeys.NewKeyHash());
        await Molly.RequestWipeAsync(fixture.Unprotect(login.ProtectedId));

        // The server secret the proof key comes from is gone, but the command still reaches the device.
        Assert.Equal(MollyCommandResult.Blocked(MollyCommand.Wipe), await Molly.IssueRegistrationTokenAsync(login.ProtectedId, MollyTestDevice.NewExchange() with { Action = MollyApiActions.RegistrationToken }, default));
    }
}
