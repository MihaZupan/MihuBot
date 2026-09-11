using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.Components.Layout;
using MihuBot.Configuration;
using MihuBot.Discord.Permissions;

namespace MihuBot.Tests;

public sealed class LayoutRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShellPreservesNavigationAndAccountVisibility(bool signedIn)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
        {
            options.AddPolicy("Discord", policy => policy.RequireAssertion(context =>
                context.User.Identity?.AuthenticationType == "Discord"));
            options.AddPolicy("GitHub", policy => policy.RequireAssertion(_ => false));
            options.AddPolicy("Admin", policy => policy.RequireAssertion(_ => false));
        });
        services.AddCascadingAuthenticationState();
        services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider(signedIn));
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<AvailableFeatures>();
        services.AddSingleton<IPermissionsService, NoPermissions>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<MainLayout>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Body"] = (RenderFragment)(builder => builder.AddContent(0, "Page content")),
            }));
            return rendered.ToHtmlString();
        });

        Assert.Contains("Skip to content", html, StringComparison.Ordinal);
        Assert.Contains("id=\"main-content\" tabindex=\"-1\"", html, StringComparison.Ordinal);
        Assert.Contains("Page content", html, StringComparison.Ordinal);
        Assert.Contains("MihuBot", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Primary navigation\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"navigation-toggle\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"primary-navigation\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"regex?pattern=Hello%20World\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"runtime-utils\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"admin\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"custom-message\"", html, StringComparison.Ordinal);
        Assert.Equal(signedIn, html.Contains("href=\"reminders\"", StringComparison.Ordinal));
        Assert.Equal(signedIn, html.Contains("href=\"Account/Logout\"", StringComparison.Ordinal));
        Assert.Equal(signedIn, html.Contains("Example &lt;user&gt;", StringComparison.Ordinal));
        Assert.Equal(!signedIn, html.Contains("Tools &amp; automation", StringComparison.Ordinal));
    }

    private sealed class TestAuthenticationStateProvider(bool signedIn) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(signedIn
                ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "Example <user>"), new Claim(ClaimTypes.NameIdentifier, "1")], "Discord")
                : new ClaimsIdentity())));
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/regex");
    }

    private sealed class NoPermissions : IPermissionsService
    {
        public bool HasPermission(string permission, ulong userId) => false;
        public ValueTask<bool> AddPermissionAsync(string permission, ulong userId) => throw new NotSupportedException();
        public ValueTask<bool> RemovePermissionAsync(string permission, ulong userId) => throw new NotSupportedException();
    }
}
