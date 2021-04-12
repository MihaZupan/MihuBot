using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
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
            _updateToken = configuration["UPDATE_TOKEN"];
        }

        [HttpGet]
        public IActionResult Deployed([FromQuery] uint runNumber, [FromQuery] string token)
        {
            _logger.DebugLog($"Received a deployment request {runNumber}");

            if (_updateToken is null)
            {
                _logger.DebugLog($"{nameof(_updateToken)} is null");
            }

            if (_updateToken is null || CheckToken(_updateToken, token))
            {
                Task.Run(async () => await RunUpdateAsync(runNumber));
            }

            return Ok();
        }

        private async Task RunUpdateAsync(uint runNumber)
        {
            try
            {
                string currentDir = Environment.CurrentDirectory;
                string nextUpdateDir = $"{currentDir}/next_update";
                string updatesDir = $"{currentDir}/updates";
                string currentUpdateDir = $"{updatesDir}/{runNumber}";

                Directory.CreateDirectory(nextUpdateDir);

                if (!Directory.Exists(currentUpdateDir))
                {
                    throw new Exception($"{currentUpdateDir} does not exist");
                }

                if (Directory.GetFiles(currentUpdateDir).Length == 0)
                {
                    throw new Exception($"{currentUpdateDir} is empty");
                }

                if (Directory.GetFiles(nextUpdateDir).Length != 0)
                {
                    throw new Exception($"{nextUpdateDir} is not empty");
                }

                Task loggerTask = _logger.DebugAsync($"Received a deployment notification for run {runNumber}");

                foreach (string path in Directory.EnumerateFiles(currentUpdateDir, "*", SearchOption.AllDirectories))
                {
                    string newPath = string.Concat(nextUpdateDir, path.AsSpan(currentUpdateDir.Length));
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                    System.IO.File.Move(path, newPath);
                }

                const int Retries = 5;
                for (int i = 1; i <= Retries; i++)
                {
                    try
                    {
                        Directory.Delete(currentUpdateDir, recursive: true);
                        break;
                    }
                    catch (IOException ioex) when (i != Retries)
                    {
                        int retryAfter = 50 * (int)Math.Pow(2, i);
                        _logger.DebugLog($"'{ioex.Message}' when trying to delete {currentUpdateDir}. Retrying in {retryAfter} ms");
                        await Task.Delay(retryAfter);
                    }
                }

                await loggerTask;

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
