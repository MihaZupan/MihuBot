using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using MihuBot.Helpers;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Azure.Core;

namespace MihuBot
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Directory.CreateDirectory(Constants.StateDirectory);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine(e.ExceptionObject);
            };

            var cts = new CancellationTokenSource();

            try
            {
                Task hostTask = CreateHostBuilder(args).Build().RunAsync(cts.Token);

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
                    TokenCredential credential = new ChainedTokenCredential(
                        new ManagedIdentityCredential(),
                        new AzureCliCredential());

                    config.AddAzureKeyVault(
                        new Uri("https://mihubotkv.vault.azure.net/"),
                        credential);
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseUrls("http://*:80", "https://*:443");
                    webBuilder.UseStartup<Startup>();
                });

        internal static readonly TaskCompletionSource BotStopTCS = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
