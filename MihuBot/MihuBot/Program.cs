using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "start")
            {
                StartUpdate();
                return;
            }

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
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseUrls("http://*:80", "https://*:443");
                    webBuilder.UseStartup<Startup>();
                });

        internal static readonly TaskCompletionSource<object> BotStopTCS =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static int _updating = 0;

        internal static void StartUpdate()
        {
            if (Interlocked.Exchange(ref _updating, 1) != 0)
                return;

            using Process updateProcess = new Process();
            updateProcess.StartInfo.FileName = "/bin/bash";
            updateProcess.StartInfo.Arguments = "update.sh";
            updateProcess.StartInfo.UseShellExecute = false;
            updateProcess.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
            updateProcess.Start();

            BotStopTCS.TrySetResult(null);
        }
    }
}
