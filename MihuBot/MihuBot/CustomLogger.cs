namespace MihuBot
{
    public class CustomLogger : IHostedService
    {
        public readonly LoggerOptions Options;
        private readonly HttpClient _http;
        private readonly IConfiguration _configuration;

        public Logger Logger;

        public CustomLogger(HttpClient httpClient, LoggerOptions options, IConfiguration configuration)
        {
            _http = httpClient;
            Options = options;
            _configuration = configuration;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Options.Discord.EnsureInitializedAsync();
            Logger = new Logger(_http, Options, _configuration);
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
