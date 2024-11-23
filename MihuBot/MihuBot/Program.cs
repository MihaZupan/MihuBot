using Azure.Identity;
using Azure.Core;
using System.Runtime.InteropServices;

namespace MihuBot;

public class Program
{
    public static bool AzureEnabled => true;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Starting ...");

        Directory.CreateDirectory(Constants.StateDirectory);

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.WriteLine($"UnhandledException: {e.ExceptionObject}");
        };

        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            cts.Cancel();
        };

        try
        {
            IHost host = CreateHostBuilder(args).Build();

            await host.RunDatabaseMigrations();

            Console.WriteLine("Starting host.RunAsync ...");

            Task hostTask = host.RunAsync(cts.Token);

            Task finishedTask = await Task.WhenAny(hostTask, BotStopTCS.Task);

            if (finishedTask != hostTask)
            {
                cts.Cancel();
                try
                {
                    await finishedTask;
                }
                catch { }
            }

            await hostTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                if (AzureEnabled)
                {
                    AzureCredential = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        ? new ManagedIdentityCredential()
                        : new AzureCliCredential();

                    config.AddAzureKeyVault(
                        new Uri("https://mihubotkv.vault.azure.net/"),
                        AzureCredential);
                }
                else
                {
                    config.AddJsonFile("credentials.json", optional: true);
                }
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                {
                    options.Limits.MaxResponseBufferSize *= 32;
                    options.Limits.Http2.InitialStreamWindowSize *= 32;
                    options.Limits.Http2.InitialConnectionWindowSize *= 32;
                });

                webBuilder.UseUrls("http://*:80", "https://*:443");
                webBuilder.UseStartup<Startup>();
            });

    internal static TokenCredential AzureCredential { get; private set; }

    internal static readonly TaskCompletionSource BotStopTCS = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
