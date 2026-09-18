using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MihuBot.API;

[ApiController]
[Route("api/[controller]")]
public sealed class SelfUpdateController(SelfUpdateService selfUpdate) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("Check")]
    public void Check() => selfUpdate.RequestUpdateCheck();
}
