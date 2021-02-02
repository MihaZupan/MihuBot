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

        public Logger Logger;

        public CustomLogger(HttpClient httpClient, LoggerOptions options)
        {
            _http = httpClient;
            _options = options;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _options.Discord.EnsureInitializedAsync();
            Logger = new Logger(_http, _options);
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
