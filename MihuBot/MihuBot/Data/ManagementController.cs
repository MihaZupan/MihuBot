using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MihuBot.Data
{
    [Route("[controller]/[action]")]
    public class ManagementController : ControllerBase
    {
        private static readonly string _updateToken = Environment.GetEnvironmentVariable("UPDATE_TOKEN");

        private readonly Logger _logger;

        public ManagementController(Logger logger)
        {
            _logger = logger;
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
                    throw new Exception($"{currentUpdateDir} is not empty");
                }

                Task loggerTask = _logger.DebugAsync($"Received a deployment notification for run {runNumber}");

                foreach (string path in Directory.EnumerateFiles(currentUpdateDir, "*", SearchOption.AllDirectories))
                {
                    string newPath = string.Concat(nextUpdateDir, path.AsSpan(currentUpdateDir.Length));
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                    System.IO.File.Move(path, newPath);
                }

                Directory.Delete(currentUpdateDir);

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
