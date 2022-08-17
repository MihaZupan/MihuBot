using Microsoft.AspNetCore.Mvc;
using MihuBot.Configuration;

namespace MihuBot.API
{
    [Route("api/[controller]/[action]")]
    public sealed class FossabotController : ControllerBase
    {
        private readonly IConfigurationService _configurationService;

        public FossabotController(IConfigurationService configurationService)
        {
            _configurationService = configurationService;
        }

        [HttpGet]
        public IActionResult Edison()
        {
            if (!string.Equals(Request.Headers.UserAgent.ToString(), "Fossabot Web Proxy", StringComparison.OrdinalIgnoreCase) ||
                !Request.Headers.TryGetValue("x-fossabot-channelprovider", out var channelProvider) ||
                !string.Equals(channelProvider.ToString(), "twitch", StringComparison.OrdinalIgnoreCase))
            {
                return Unauthorized();
            }

            if (Request.Headers.TryGetValue("x-fossabot-message-userlogin", out var value) &&
                _configurationService.TryGet(null, $"Fossabot.EdisonCustomResponse.{value}", out string customResponse))
            {
                return Ok(customResponse);
            }

            if (_configurationService.TryGet(null, "Fossabot.EdisonCustomResponse", out string defaultResponse))
            {
                return Ok(defaultResponse);
            }

            return Ok("Hi");
        }
    }
}
