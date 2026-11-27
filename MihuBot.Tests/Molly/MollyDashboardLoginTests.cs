using System.Buffers.Text;
using Microsoft.Extensions.Time.Testing;
using MihuBot.Molly;
using MihuBot.Molly.Api;

namespace MihuBot.Tests.Molly;

public sealed class MollyDashboardLoginTests(MollyServiceFixture fixture) : IClassFixture<MollyServiceFixture>
{
    [Fact]
    public async Task DashboardLinks_RequireOptIn_AndExpireAfterFiveMinutes()
    {
        var time = new FakeTimeProvider();
        MollyService service = fixture.CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey, timeProvider: time);
        MollyLoginResult login = await fixture.LoginAsync(MollyTestKeys.NewKeyHash(), service);
        Guid id = fixture.Unprotect(login.ProtectedId);
        await service.AssociateAsync(login.ProtectedId, "device");

        MollyCommandResult result = await service.IssueDashboardLoginAsync(login.ProtectedId);
        Assert.Equal(MollyResultStatus.Ok, result.Status);
        Assert.Null(result.DashboardLoginToken);
        Assert.False((await fixture.GetEntryAsync(id)).DashboardLoginAllowed);

        await service.SetDashboardLoginAllowedAsync(id, true);
        result = await service.IssueDashboardLoginAsync(login.ProtectedId);
        string token = Assert.IsType<string>(result.DashboardLoginToken);
        Assert.True(await service.TryDashboardLoginAsync(token));

        time.Advance(MollyDashboardLoginLinks.Lifetime - TimeSpan.FromTicks(1));
        Assert.True(await service.TryDashboardLoginAsync(token));

        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(await service.TryDashboardLoginAsync(token));
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("lock")]
    [InlineData("wipe")]
    [InlineData("delete")]
    public async Task RevokedOrBlockedDevices_CannotIssueOrRedeemLinks(string action)
    {
        MollyLoginResult login = await fixture.LoginAsync(MollyTestKeys.NewKeyHash());
        Guid id = fixture.Unprotect(login.ProtectedId);
        await fixture.Service.AssociateAsync(login.ProtectedId, "device");
        await fixture.Service.SetDashboardLoginAllowedAsync(id, true);
        MollyCommandResult result = await fixture.Service.IssueDashboardLoginAsync(login.ProtectedId);
        string token = Assert.IsType<string>(result.DashboardLoginToken);

        switch (action)
        {
            case "disable": await fixture.Service.SetDashboardLoginAllowedAsync(id, false); break;
            case "lock": await fixture.Service.SetLockRequestedAsync(id, true); break;
            case "wipe": await fixture.Service.RequestWipeAsync(id); break;
            case "delete": await fixture.Service.DeleteEntryAsync(id); break;
        }

        Assert.False(await fixture.Service.TryDashboardLoginAsync(token));
        Assert.Null((await fixture.Service.IssueDashboardLoginAsync(login.ProtectedId)).DashboardLoginToken);

        if (action == "wipe")
        {
            await fixture.Service.SetDashboardLoginAllowedAsync(id, true);
            Assert.False((await fixture.GetEntryAsync(id)).DashboardLoginAllowed);
        }
    }

    [Fact]
    public async Task DashboardLinks_RejectInvalidAndSupersededSessions()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult oldLogin = await fixture.LoginAsync(keyHash);
        await fixture.Service.AssociateAsync(oldLogin.ProtectedId, "device");
        await fixture.Service.SetDashboardLoginAllowedAsync(fixture.Unprotect(oldLogin.ProtectedId), true);
        MollyLoginResult newLogin = await fixture.LoginAsync(keyHash);
        Assert.NotNull((await fixture.Service.IssueDashboardLoginAsync(newLogin.ProtectedId)).DashboardLoginToken);

        foreach (string? session in new[] { null, "invalid", oldLogin.ProtectedId })
        {
            Assert.Equal(MollyCommandResult.Invalid, await fixture.Service.IssueDashboardLoginAsync(session));
        }

        Assert.False(await fixture.Service.TryDashboardLoginAsync(newLogin.ProtectedId));
    }

    [Theory]
    [InlineData(MollyCommand.Lock)]
    [InlineData(MollyCommand.Wipe)]
    public async Task BlockingCommands_AreDeliveredWithoutAProof(MollyCommand command)
    {
        MollyLoginResult login = await fixture.LoginAsync(MollyTestKeys.NewKeyHash());
        Guid id = fixture.Unprotect(login.ProtectedId);

        if (command == MollyCommand.Lock)
        {
            await fixture.Service.SetLockRequestedAsync(id, true);
        }
        else
        {
            await fixture.Service.RequestWipeAsync(id);
        }

        var request = new MollyDashboardLoginRequest { Id = login.ProtectedId };
        Assert.Equal(MollyCommandResult.Blocked(command), await fixture.Service.IssueDashboardLoginAsync(request, MollyTestDevice.NewExchange(), default));
    }

    [Fact]
    public void DashboardLinks_RejectTamperingAndOtherInstances()
    {
        var time = new FakeTimeProvider();
        var links = new MollyDashboardLoginLinks(time);
        Guid id = Guid.NewGuid();
        string token = links.Issue(id);
        Assert.True(links.TryGetEntryId(token, out Guid recovered));
        Assert.Equal(id, recovered);

        Assert.False(new MollyDashboardLoginLinks(time).TryGetEntryId(token, out _));

        byte[] bytes = Base64Url.DecodeFromChars(token);
        bytes[^1] ^= 1;

        foreach (string? invalid in new[] { null, "", "invalid", token + "=", Base64Url.EncodeToString(bytes) })
        {
            Assert.False(links.TryGetEntryId(invalid, out _));
        }
    }
}
