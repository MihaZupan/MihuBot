using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.RegularExpressions;
using MihuBot.Components;
using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

public sealed class MollyDashboardTests(MollyServiceFixture fixture) : MollyShareServiceTestBase(fixture)
{
    [Fact]
    public async Task BootKeyIsVisibleForUnknownDevices_AndOnHoverForKnownDevices()
    {
        const string KnownBootKey = "F0A890375D1405E62EBFD87E8D3F475F948EF031BBF9DDD516D5F600A23677E8";
        var knownDevice = new MollyTestDevice(verifiedBootKey: Convert.FromHexString(KnownBootKey));
        var unknownDevice = new MollyTestDevice();

        foreach (MollyTestDevice device in new[] { knownDevice, unknownDevice })
        {
            MollyLoginResult login = await _fixture.LoginAsync(MollyTestKeys.NewKeyHash(), device: device);
            Assert.Equal(MollyResultStatus.Ok, login.Status);
            Assert.Equal(MollyCommandResult.Ok() with { Identity = MollyIdentityBinding.Bound }, await _fixture.Service.AssociateAsync(login.ProtectedId, "device"));
        }

        string html = await RenderDashboardAsync();
        Assert.Contains("Google Pixel 6, GrapheneOS", html, StringComparison.Ordinal);
        Assert.Contains($"title=\"{KnownBootKey}\"", html, StringComparison.Ordinal);
        Assert.DoesNotMatch($@"<code[^>]*>{KnownBootKey}</code>", html);
        Assert.Contains("Unknown model", html, StringComparison.Ordinal);
        Assert.Matches($@"<code[^>]*>{Convert.ToHexString(unknownDevice.VerifiedBootKey)}</code>", html);
    }

    [Fact]
    public async Task ShareCountsUseSingularAndPlural()
    {
        Party owner = await NewPartyAsync();
        Party recipient = await NewPartyAsync();
        Party trustee = await NewPartyAsync();

        string html = await RenderDashboardAsync();
        Assert.Matches(@"Shared: 0 shares\.\s+Received:", html);
        Assert.Contains("0 trustee requests to approve", html, StringComparison.Ordinal);

        var first = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1);
        Assert.Equal("ok", await CreateAsync(owner, first));
        await StartAsync(recipient, first);

        html = await RenderDashboardAsync();
        Assert.Matches(@"Shared: 1 share\.\s+Received:", html);
        Assert.Contains("1 trustee request to approve", html, StringComparison.Ordinal);
        Assert.Contains("(1 trustee)", html, StringComparison.Ordinal);

        await ReserveEntryAsync(owner);
        html = await RenderDashboardAsync();
        Assert.Matches(@"Shared: 1 share, 1 reserved for creation\.\s+Received:", html);

        Party otherTrustee = await NewPartyAsync();
        var second = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity, otherTrustee.Identity], 1);
        Assert.Equal("ok", await CreateAsync(owner, second));
        await StartAsync(recipient, second);

        html = await RenderDashboardAsync();
        Assert.Contains("Shared: 2 shares", html, StringComparison.Ordinal);
        Assert.Contains("2 trustee requests to approve", html, StringComparison.Ordinal);
        Assert.Contains("(2 trustees)", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_DoesNotExposeDeviceEntryIds_AndDetailsTogglesHaveLabels(bool isAdmin)
    {
        Party first = await NewPartyAsync();
        Party linked = await NewPartyAsync(bind: false);
        Assert.Equal(MollyResultStatus.Ok, (await _client.AssociateAsync(first.Session, first.Identity, "First device")).Status);
        Assert.Equal(MollyResultStatus.Ok, (await _client.AssociateAsync(linked.Session, first.Identity, "Linked device")).Status);

        MollyUserInfo[] users = await _fixture.Service.GetRegisteredUsersAsync();
        string html = await RenderDashboardAsync(isAdmin);

        foreach (MollyUserInfo user in users)
        {
            Assert.DoesNotContain(user.Id.ToString("D"), html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(user.Id.ToString("N"), html, StringComparison.OrdinalIgnoreCase);
        }

        MatchCollection toggles = Regex.Matches(html, @"<input\b[^>]*class=""details-toggle visually-hidden""[^>]*>");
        MatchCollection labeledToggles = Regex.Matches(html, @"<label\b[^>]*>\s*<input\b[^>]*class=""details-toggle visually-hidden""[^>]*>[\s\S]*?</label>");
        Assert.Equal(users.Length, toggles.Count);
        Assert.Equal(toggles.Count, labeledToggles.Count);

        if (isAdmin)
        {
            Assert.Contains("also bound on First device", html, StringComparison.Ordinal);
            Assert.Contains("also bound on Linked device", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MissingLinkedDevice_DoesNotExposeItsEntryId()
    {
        var dashboard = new MollyDashboard();
        MethodInfo nicknameOf = typeof(MollyDashboard).GetMethod("NicknameOf", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal("Unknown device", nicknameOf.Invoke(dashboard, [Guid.NewGuid()]));
    }

    [Fact]
    public async Task DashboardLoginOptIn_IsOnlyShownToAdmins()
    {
        Party device = await NewPartyAsync();

        string html = await RenderDashboardAsync();
        Assert.DoesNotContain("Allow dashboard login", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Disable dashboard login", html, StringComparison.Ordinal);

        html = await RenderDashboardAsync(isAdmin: true);
        Assert.Contains("Allow dashboard login", html, StringComparison.Ordinal);

        await _fixture.Service.SetDashboardLoginAllowedAsync(_fixture.Unprotect(device.Session), true);
        html = await RenderDashboardAsync(isAdmin: true);
        Assert.Contains("Disable dashboard login", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonAdminDashboard_CannotChangeTheDashboardLoginOptIn()
    {
        Party device = await NewPartyAsync();
        Guid id = _fixture.Unprotect(device.Session);
        var dashboard = new MollyDashboard();
        MethodInfo handler = typeof(MollyDashboard).GetMethod("SetDashboardLoginAllowedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (bool allowed in new[] { true, false })
        {
            await _fixture.Service.SetDashboardLoginAllowedAsync(id, !allowed);
            MollyUserInfo user = (await _fixture.Service.GetRegisteredUsersAsync()).First(u => u.Id == id);
            await (Task)handler.Invoke(dashboard, [user, allowed])!;
            Assert.Equal(!allowed, (await _fixture.GetEntryAsync(id)).DashboardLoginAllowed);
        }
    }

    private async Task<string> RenderDashboardAsync(bool isAdmin = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_fixture.Service);
        services.AddSingleton(_shares);
        services.AddSingleton(_protector);
        services.AddSingleton<NavigationManager, TestNavigationManager>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<MollyDashboard>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(MollyDashboard.IsAdmin)] = isAdmin }));
            return rendered.ToHtmlString();
        });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/molly");
    }
}
