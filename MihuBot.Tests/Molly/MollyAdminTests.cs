using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MihuBot.Components.Pages;
using MihuBot.Configuration;
using MihuBot.Helpers;
using MihuBot.Molly;

#pragma warning disable BL0006 // An interactive renderer exercises circuit-only login behavior.

namespace MihuBot.Tests.Molly;

public sealed class MollyAdminTests(MollyServiceFixture fixture) : IClassFixture<MollyServiceFixture>
{
    [Fact]
    public async Task ClientLink_LogsInWithoutAdminRights_RemovesQuery_AndKeepsTheCircuitLoggedIn()
    {
        var time = new FakeTimeProvider();
        MollyService service = fixture.CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey, timeProvider: time);
        (string token, _) = await IssueLinkAsync(service);
        var navigation = new TestNavigationManager(token);

        await using ServiceProvider provider = CreateServices(service, navigation);
        await using var renderer = new TestRenderer(provider, isInteractive: true);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPage page = renderer.CreatePage();
            SetProperty(page, "LoginToken", token);
            await page.InitializeAsync();
            await page.ApplyParametersAsync();

            Assert.True(GetField<bool>(page, "_unlocked"));
            Assert.False(GetField<bool>(page, "_isAdmin"));
            Assert.Equal("https://localhost/molly?other=kept", navigation.Uri);
            Assert.True(navigation.ReplacedHistory);

            time.Advance(TimeSpan.FromMinutes(5));
            SetProperty(page, "LoginToken", null);
            await page.ApplyParametersAsync();
            Assert.True(GetField<bool>(page, "_unlocked"));
        });
    }

    [Theory]
    [InlineData(true, "invalid")]
    [InlineData(true, "expired")]
    [InlineData(true, "disabled")]
    [InlineData(false, "valid")]
    public async Task InvalidLinksAndPrerendering_DoNotUnlockTheDashboard(bool isInteractive, string state)
    {
        var time = new FakeTimeProvider();
        MollyService service = fixture.CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey, timeProvider: time);
        (string token, Guid id) = await IssueLinkAsync(service);

        if (state == "invalid")
        {
            token = "invalid";
        }
        else if (state == "expired")
        {
            time.Advance(TimeSpan.FromMinutes(5));
        }
        else if (state == "disabled")
        {
            await service.SetDashboardLoginAllowedAsync(id, false);
        }

        await using ServiceProvider provider = CreateServices(service, new TestNavigationManager(token));
        await using var renderer = new TestRenderer(provider, isInteractive);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPage page = renderer.CreatePage();
            SetProperty(page, "LoginToken", token);
            await page.InitializeAsync();
            await page.ApplyParametersAsync();
            Assert.False(GetField<bool>(page, "_unlocked"));
            Assert.False(GetField<bool>(page, "_isAdmin"));
        });
    }

    [Fact]
    public async Task PasswordAndAdminLogins_KeepTheirExistingPermissions()
    {
        await using ServiceProvider provider = CreateServices(fixture.Service, new TestNavigationManager(""), new TestConfiguration("first, second"));
        await using var renderer = new TestRenderer(provider, isInteractive: true);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPage passwordPage = renderer.CreatePage();
            await passwordPage.InitializeAsync();
            object model = GetField<object>(passwordPage, "Login");
            model.GetType().GetProperty("Password")!.SetValue(model, "second");
            typeof(MollyAdmin).GetMethod("Unlock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(passwordPage, null);
            Assert.True(GetField<bool>(passwordPage, "_unlocked"));
            Assert.False(GetField<bool>(passwordPage, "_isAdmin"));

            TestPage adminPage = renderer.CreatePage();
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Constants.Admins.First().ToString(System.Globalization.CultureInfo.InvariantCulture))], "Discord"));
            await adminPage.InitializeAsync(principal);
            Assert.True(GetField<bool>(adminPage, "_unlocked"));
            Assert.True(GetField<bool>(adminPage, "_isAdmin"));
        });
    }

    private async Task<(string Token, Guid Id)> IssueLinkAsync(MollyService service)
    {
        var device = new MollyTestDevice();
        MollyLoginResult login = await fixture.LoginAsync(MollyTestKeys.NewKeyHash(), service, device);
        await service.AssociateAsync(login.ProtectedId, "device");
        Guid id = fixture.Unprotect(login.ProtectedId);
        await service.SetDashboardLoginAllowedAsync(id, true);
        MollyCommandResult result = await service.IssueDashboardLoginAsync(login.ProtectedId);
        return (Assert.IsType<string>(result.DashboardLoginToken), id);
    }

    private static ServiceProvider CreateServices(MollyService service, NavigationManager navigation, IConfigurationService? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(service);
        services.AddSingleton(navigation);
        services.AddSingleton(configuration ?? new TestConfiguration());
        return services.BuildServiceProvider();
    }

    private static void SetProperty(MollyAdmin page, string name, object? value) =>
        typeof(MollyAdmin).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static T GetField<T>(MollyAdmin page, string name) =>
        (T)typeof(MollyAdmin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private sealed class TestPage : MollyAdmin
    {
        public Task InitializeAsync(ClaimsPrincipal? principal = null)
        {
            SetProperty(this, "AuthenticationState", Task.FromResult(new AuthenticationState(principal ?? new ClaimsPrincipal())));
            return base.OnInitializedAsync();
        }

        public Task ApplyParametersAsync() => base.OnParametersSetAsync();
    }

    private sealed class TestRenderer(IServiceProvider services, bool isInteractive) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        protected override RendererInfo RendererInfo => new("Test", isInteractive);

        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            componentActivator.CreateInstance(componentType);

        public TestPage CreatePage()
        {
            var page = (TestPage)InstantiateComponent(typeof(TestPage));
            AssignRootComponentId(page);
            return page;
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => throw exception;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public bool ReplacedHistory { get; private set; }

        public TestNavigationManager(string token) => Initialize("https://localhost/", $"https://localhost/molly?login={token}&other=kept");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            ReplacedHistory = options.ReplaceHistoryEntry;
        }
    }

    private sealed class TestConfiguration(string? password = null) : IConfigurationService
    {
        public void Set(ulong? context, string key, string value) => throw new NotSupportedException();

        public bool Remove(ulong? context, string key) => throw new NotSupportedException();

        public bool TryGet(ulong? context, string key, out string value)
        {
            value = password ?? "";
            return password is not null && string.Equals(key, "Molly.DashboardPassword", StringComparison.Ordinal);
        }
    }
}
