using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace MihuBot.Data;

[Route("[controller]")]
public class AccountController : ControllerBase
{
    [HttpGet("Login/{provider}")]
    public IActionResult Login([FromRoute] string provider, [FromQuery] string returnUrl = "/")
    {
        if (provider is not ("Discord" or "GitHub"))
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
