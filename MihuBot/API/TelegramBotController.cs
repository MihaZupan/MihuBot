using Microsoft.AspNetCore.Mvc;
using MihuBot.Data;
using Newtonsoft.Json;
using System.Security.Cryptography;
using Telegram.Bot.Types;

namespace MihuBot.API;

[Route("api/[controller]/[action]")]
public sealed class TelegramBotController : ControllerBase
{
    internal static string WebhookPath { get; } = $"https://mihubot.xyz/api/TelegramBot/{nameof(Update)}";
    internal static string WebhookUpdateSecret { get; } = RandomNumberGenerator.GetHexString(64);

    private readonly TelegramService _telegram;

    public TelegramBotController(TelegramService telegram)
    {
        _telegram = telegram;
    }

    [HttpPost]
    public async Task<IActionResult> Update()
    {
        if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var secretToken) ||
            secretToken.Count != 1 ||
            !ManagementController.CheckToken(WebhookUpdateSecret, secretToken.ToString()))
        {
            return Unauthorized();
        }

        using var reader = new StreamReader(Request.Body);
        string json = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        Update update = JsonConvert.DeserializeObject<Update>(json);

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => _telegram.HandleUpdateAsync(update));
        }

        return Ok();
    }
}
