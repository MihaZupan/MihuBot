using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.Components;

namespace MihuBot.Tests.Molly;

public sealed class MollyDashboardTests(MollyServiceFixture fixture) : MollyShareServiceTestBase(fixture)
{
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

    private async Task<string> RenderDashboardAsync()
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
            var rendered = await renderer.RenderComponentAsync<MollyDashboard>();
            return rendered.ToHtmlString();
        });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/molly");
    }
}
