using MihuBot.NextCloud;

namespace MihuBot
{
    public class CustomLogger : IHostedService
    {
        public readonly LoggerOptions Options;
        private readonly HttpClient _http;
        private readonly NextCloudClient _nextCloudClient;
        private readonly IConfiguration _configuration;

        public Logger Logger;

        public CustomLogger(HttpClient httpClient, LoggerOptions options, NextCloudClient nextCloudClient, IConfiguration configuration)
        {
            _http = httpClient;
            Options = options;
            _nextCloudClient = nextCloudClient;
            _configuration = configuration;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Options.Discord.EnsureInitializedAsync();
            Logger = new Logger(_http, Options, _nextCloudClient, _configuration);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Logger?.OnShutdownAsync();

            try
            {
                await Options.Discord.StopAsync();
            }
            catch { }
        }
    }
}
