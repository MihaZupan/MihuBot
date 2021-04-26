using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace MihuBot.Data
{
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

        [HttpGet]
        public async Task<IActionResult> Deployed([FromQuery] uint runNumber)
        {
            _logger.DebugLog($"Received a deployment request {runNumber}");

            if (!Request.Headers.TryGetValue("X-Update-Token", out var updateToken))
            {
                _logger.DebugLog($"No X-Update-Token header received");
                return Ok();
            }

            if (CheckToken(_updateToken, updateToken))
            {
                await RunUpdateAsync(runNumber);
            }

            return Ok();
        }

        private async Task RunUpdateAsync(uint runNumber)
        {
            try
            {
                string currentDir = Environment.CurrentDirectory;
                string nextUpdateDir = $"{currentDir}/next_update";
                Directory.CreateDirectory(nextUpdateDir);

                if (Directory.GetFiles(nextUpdateDir).Length != 0)
                {
                    await _logger.DebugAsync($"{nextUpdateDir} is not empty");
                    Directory.Delete(nextUpdateDir, true);
                }

                _logger.DebugLog($"Received a deployment notification for run {runNumber}");

                using var archive = new ZipArchive(Request.Body, ZipArchiveMode.Read, true);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    const string Prefix = "artifacts/";

                    if (!entry.FullName.StartsWith(Prefix))
                    {
                        await _logger.DebugAsync($"{entry.FullName} does not have a valid prefix");
                        return;
                    }

                    string path = entry.FullName.Substring(Prefix.Length);
                    string destinationPath = Path.GetFullPath(Path.Combine(nextUpdateDir, path));

                    _logger.DebugLog($"Extracting {path} for {runNumber} to {destinationPath}");
                    entry.ExtractToFile(Path.Combine(nextUpdateDir, destinationPath));
                }

                Program.BotStopTCS.TrySetResult();
            }
            catch (Exception ex)
            {
                try
                {
                    await _logger.DebugAsync($"Failed to deploy an update for {runNumber}: {ex}");
                }
                catch { }
            }
        }

        private static bool CheckToken(string expected, string actual)
        {
            if (expected is null || actual is null)
                return false;

            if (expected.Length != actual.Length)
                return false;

            int differentbits = 0;
            for (int i = 0; i < expected.Length; ++i)
            {
                differentbits |= expected[i] ^ actual[i];
            }
            return differentbits == 0;
        }
    }
}
