using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace MihuBot.API;

[Route("[controller]")]
public class AccountController : ControllerBase
{
    [HttpGet("Login/{provider}")]
    public async Task<IActionResult> Login([FromRoute] string provider, [FromQuery] string returnUrl = "/")
    {
        if (provider is not ("Discord" or "GitHub"))
        {
            return NotFound();
        }

        // The scheme isn't registered when the provider isn't configured.
        var schemes = HttpContext.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await schemes.GetSchemeAsync(provider) is null)
        {
            return NotFound();
        }

        return Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, provider);
    }

    [HttpGet("Logout")]
    public async Task<IActionResult> Logout(string returnUrl = "/")
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect(returnUrl);
    }
}
