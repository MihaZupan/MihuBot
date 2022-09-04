namespace MihuBot;

public class DDsLogger : CustomLogger
{
    public DDsLogger(HttpClient httpClient, LoggerOptions options, IConfiguration configuration)
        : base(httpClient, options, configuration)
    { }
}

public class PrivateLogger : CustomLogger
{
    public PrivateLogger(HttpClient httpClient, LoggerOptions options, IConfiguration configuration)
        : base(httpClient, options, configuration)
    { }
}

public class CustomLogger : IHostedService
{
    public readonly LoggerOptions Options;

    public Logger Logger;

    public CustomLogger(HttpClient httpClient, LoggerOptions options, IConfiguration configuration)
    {
        Options = options;
        Logger = new Logger(httpClient, Options, configuration);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Options.Discord.EnsureInitializedAsync();
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
