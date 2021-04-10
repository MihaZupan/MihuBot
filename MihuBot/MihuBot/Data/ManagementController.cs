using Microsoft.AspNetCore.Mvc;
using System;
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
        public void Deployed([FromQuery] uint runNumber, [FromQuery] string token)
        {
            if (CheckToken(_updateToken, token))
            {
                Task.Run(async () => await RunUpdateAsync(runNumber));
            }
        }

        private async Task RunUpdateAsync(uint runNumber)
        {
            try
            {
                await _logger.DebugAsync($"Received a deployment notification for run {runNumber}");
            }
            catch { }
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
