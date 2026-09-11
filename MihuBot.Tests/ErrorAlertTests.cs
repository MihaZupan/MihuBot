using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.Components;
using MihuBot.Helpers;

namespace MihuBot.Tests;

public sealed class ErrorAlertTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Discord")]
    [InlineData("GitHub")]
    public async Task NonAdminsSeeNoExceptionDetails(string? authenticationType)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, authenticationType == "GitHub" ? Constants.Admins.First().ToString() : "1")],
            authenticationType));

        string html = await RenderAsync(user, CreateException());

        Assert.Contains("An unexpected error occurred. Please try again later.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("private", html, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), html, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(CreateException), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminsSeeFullExceptionDetails()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Constants.Admins.First().ToString())], "Discord"));
        Exception error = CreateException();

        string html = await RenderAsync(user, error);

        Assert.Contains(error.ToString(), WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationMessagesRemainVisibleAndHtmlEncoded()
    {
        string html = await RenderAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, "Invalid <URL>");

        Assert.Contains("Invalid &lt;URL&gt;", html, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoErrorRendersNoAlert()
    {
        string html = await RenderAsync(new ClaimsPrincipal(new ClaimsIdentity()), null);

        Assert.DoesNotContain("role=\"alert\"", html, StringComparison.Ordinal);
    }

    private static Exception CreateException()
    {
        try
        {
            throw new InvalidOperationException("private outer message", new Exception("private inner message"));
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static async Task<string> RenderAsync(ClaimsPrincipal user, Exception? error, string? message = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
            options.AddPolicy("Admin", policy => policy.RequireAssertion(context => context.User.IsAdmin())));
        services.AddCascadingAuthenticationState();
        services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider(user));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ErrorAlert>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ErrorAlert.Error)] = error,
                [nameof(ErrorAlert.Message)] = message,
            }));
            return rendered.ToHtmlString();
        });
    }

    private sealed class TestAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }
}
