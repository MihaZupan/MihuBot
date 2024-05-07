using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MihuBot.API;

[Route("api/[controller]")]
[ApiController]
public sealed class RunScriptController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, string> s_scripts = new();

    [HttpGet]
    public async Task GetScript([FromQuery] string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!s_scripts.TryGetValue(id, out string script))
        {
            if (id == "test")
            {
                script = $"echo 'Hello world'";
            }
            else
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        await Response.WriteAsync(script);
    }

    public static string AddScript(string script, TimeSpan expiration)
    {
        string id = RandomNumberGenerator.GetHexString(32);
        s_scripts.TryAdd(id, script);
        Task.Delay(expiration).ContinueWith(_ => s_scripts.TryRemove(id, out string _));
        return id;
    }
}
