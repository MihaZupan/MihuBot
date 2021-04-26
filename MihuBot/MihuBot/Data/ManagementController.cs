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

        [HttpPost]
        [RequestSizeLimit(256 * 1024 * 1024)]
        public async Task<IActionResult> Deployed()
        {
            if (!Request.Headers.TryGetValue("X-Run-Number", out var runNumberValue) || !uint.TryParse(runNumberValue, out uint runNumber))
            {
                _logger.DebugLog($"No X-Run-Number header received");
                return Unauthorized();
            }

            if (!Request.Headers.TryGetValue("X-Update-Token", out var updateToken))
            {
                _logger.DebugLog($"No X-Update-Token header received");
                return Unauthorized();
            }

            if (!CheckToken(_updateToken, updateToken))
            {
                return Unauthorized();
            }

            await RunUpdateAsync(runNumber);
            return Ok();
        }

        private async Task RunUpdateAsync(uint runNumber)
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), $"{runNumber}.zip");
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

                using (var tempFs = System.IO.File.OpenWrite(tempFilePath))
                {
                    await Request.Body.CopyToAsync(tempFs);
                }

                using var archiveFs = System.IO.File.OpenRead(tempFilePath);
                using var archive = new ZipArchive(archiveFs, ZipArchiveMode.Read, true);
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
            finally
            {
                System.IO.File.Delete(tempFilePath);
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
