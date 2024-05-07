using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MihuBot.API;

[Route("api/[controller]")]
[ApiController]
public sealed class RunScriptController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, Func<string, string>> s_scripts = new();

    [HttpGet]
    public async Task GetScript([FromQuery] string id, [FromQuery] string token)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!s_scripts.TryGetValue(id, out var generator))
        {
            if (id == "test")
            {
                generator = token => $"echo 'Hello world. Token length: {token.Length}'";
            }
            else
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        await Response.WriteAsync(generator(token));
    }

    public static string AddScript(Func<string, string> generator, TimeSpan expiration)
    {
        string id = RandomNumberGenerator.GetHexString(32);
        s_scripts.TryAdd(id, generator);
        Task.Delay(expiration).ContinueWith(_ => s_scripts.TryRemove(id, out Func<string, string> _));
        return id;
    }
}
