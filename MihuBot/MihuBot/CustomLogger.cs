using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    public class CustomLogger : IHostedService
    {
        private readonly HttpClient _http;
        private readonly LoggerOptions _options;
        private readonly IConfiguration _configuration;

        public Logger Logger;

        public CustomLogger(HttpClient httpClient, LoggerOptions options, IConfiguration configuration)
        {
            _http = httpClient;
            _options = options;
            _configuration = configuration;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _options.Discord.EnsureInitializedAsync();
            Logger = new Logger(_http, _options, _configuration);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Logger?.OnShutdownAsync();

            try
            {
                await _options.Discord.StopAsync();
            }
            catch { }
        }
    }
}
