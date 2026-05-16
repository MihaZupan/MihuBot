using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;

namespace MihuBot.Data;

[Route("[controller]/[action]")]
public class ManagementController(IConfiguration configuration) : ControllerBase
{
    private readonly string _updateToken = configuration["UPDATE-TOKEN"] ?? throw new ArgumentNullException(nameof(configuration), "Missing update token.");

    [HttpPost]
    [RequestSizeLimit(256 * 1024 * 1024)]
    public async Task<IActionResult> Deployed()
    {
        if (!Request.Headers.TryGetValue("X-Run-Number", out var runNumberValue) || !uint.TryParse(runNumberValue, out uint runNumber))
        {
            return Unauthorized();
        }

        if (!Request.Headers.TryGetValue("X-Update-Token", out var updateToken))
        {
            return Unauthorized();
        }

        if (!CryptographicOperations.FixedTimeEquals(_updateToken, updateToken))
        {
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

            Console.WriteLine($"Received a deployment notification for run {runNumber}");

            await using (FileStream fs = System.IO.File.OpenWrite(artifactsPath))
            {
                await Request.Body.CopyToAsync(fs);
            }

            Lifetime.StopTCS.TrySetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to deploy an update for {runNumber}: {ex}");
            throw;
        }
    }
}
