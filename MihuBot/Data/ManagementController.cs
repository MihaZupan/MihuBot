using Microsoft.AspNetCore.Mvc;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihuBot.Data;

[Route("[controller]/[action]")]
public class ManagementController : ControllerBase
{
    private readonly Logger _logger;
    private readonly string _updateToken;

    public ManagementController(Logger logger, IConfiguration configuration)
    {
        _logger = logger;
        _updateToken = configuration["UPDATE-TOKEN"];
    }

    [HttpPost]
    [RequestSizeLimit(256 * 1024 * 1024)]
    public async Task<IActionResult> Deployed()
    {
        if (!Request.Headers.TryGetValue("X-Run-Number", out var runNumberValue) || !uint.TryParse(runNumberValue, out uint runNumber))
        {
            _logger.DebugLog("No X-Run-Number header received");
            return Unauthorized();
        }

        if (!Request.Headers.TryGetValue("X-Update-Token", out var updateToken))
        {
            _logger.DebugLog("No X-Update-Token header received");
            return Unauthorized();
        }

        if (!CheckToken(_updateToken, updateToken))
        {
            _logger.DebugLog("Invalid X-Update-Token received");
            return Unauthorized();
        }

        await RunUpdateAsync(runNumber);
        return Ok();
    }

    private async Task RunUpdateAsync(uint runNumber)
    {
        try
        {
            string currentDir = Environment.CurrentDirectory;
            string nextUpdateDir = $"{currentDir}/next_update";
            Directory.CreateDirectory(nextUpdateDir);
            string artifactsPath = Path.Combine(nextUpdateDir, "artifacts.tar.gz");

            System.IO.File.Delete(artifactsPath);

            _logger.DebugLog($"Received a deployment notification for run {runNumber}");

            await using (FileStream fs = System.IO.File.OpenWrite(artifactsPath))
            {
                await Request.Body.CopyToAsync(fs);
            }

            ProgramState.BotStopTCS.TrySetResult();
        }
        catch (Exception ex)
        {
            try
            {
                await _logger.DebugAsync($"Failed to deploy an update for {runNumber}", ex);
            }
            catch { }
        }
    }

    public static bool CheckToken(string expected, string actual)
    {
        ArgumentException.ThrowIfNullOrEmpty(expected);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expected.Length, 1000);

        if (actual is null || expected.Length != actual.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.Cast<char, byte>(expected),
            MemoryMarshal.Cast<char, byte>(actual));
    }
}
