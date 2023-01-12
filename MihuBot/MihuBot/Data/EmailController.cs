using Microsoft.AspNetCore.Mvc;
using MihuBot.Configuration;

namespace MihuBot.Data;

[Route("[controller]/[action]")]
public class EmailController : ControllerBase
{
    private readonly DiscordSocketClient _discord;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;

    public EmailController(DiscordSocketClient discord, Logger logger, IConfigurationService configuration)
    {
        _discord = discord;
        _logger = logger;
        _configuration = configuration;
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        if (!Request.Headers.TryGetValue("X-Email-Api-Key", out var apiKey))
        {
            _logger.DebugLog("No X-Email-Api-Key header received");
            return Unauthorized();
        }

        if (!_configuration.TryGet(null, "Email-Api-Key", out string actualKey))
        {
            _logger.DebugLog("No Email-Api-Key in config");
            return Unauthorized();
        }

        if (!ManagementController.CheckToken(actualKey, apiKey))
        {
            _logger.DebugLog("Invalid X-Email-Api-Key received");
            return Unauthorized();
        }

        if (!_configuration.TryGet(null, "Email-ChannelId", out string emailChannelId) ||
            !ulong.TryParse(emailChannelId, out ulong channelId) ||
            _discord.GetTextChannel(channelId) is not SocketTextChannel channel)
        {
            await _logger.DebugAsync("No or invalid Email-ChannelId in config");
            return Problem("No email channel");
        }

        try
        {
            using var reader = new StreamReader(Request.Body);
            string json = await reader.ReadToEndAsync(HttpContext.RequestAborted);

            await channel.SendTextFileAsync(json, "Email.json");
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"Failed to send email to channel: '{ex}'");
        }

        return Ok();
    }
}
