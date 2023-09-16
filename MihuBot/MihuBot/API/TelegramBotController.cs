﻿using Microsoft.AspNetCore.Mvc;
using MihuBot.Data;
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

    public IActionResult Update(Update update)
    {
        if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var secretToken) ||
            secretToken.Count != 1 ||
            !ManagementController.CheckToken(WebhookUpdateSecret, secretToken.ToString()))
        {
            return Unauthorized();
        }

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => _telegram.HandleUpdateAsync(update));
        }

        return Ok();
    }
}
