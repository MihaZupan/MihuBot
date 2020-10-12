using Discord.WebSocket;
using LettuceEncrypt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MihuBot.Helpers;
using MihuBot.Reminders;
using StackExchange.Redis;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace MihuBot
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                DirectoryInfo certDir = new DirectoryInfo("/home/certs");
                certDir.Create();

                services.AddLettuceEncrypt()
                    .PersistDataToDirectory(certDir, "certpass123");
            }

            var http = new HttpClient();
            services.AddSingleton(http);

            services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect($"{Secrets.RedisDatabaseAddress},password={Secrets.RedisDatabasePassword}"));

            var discord = new DiscordSocketClient(
                new DiscordSocketConfig()
                {
                    MessageCacheSize = 1024 * 16,
                    ConnectionTimeout = 30_000
                });
            services.AddSingleton(discord);

            services.AddSingleton(new Logger(http, discord));

            services.AddSingleton<IReminderService, ReminderService>();

            services.AddHostedService<MihuBotService>();

            services.AddRazorPages();
            services.AddServerSideBlazor();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        }
    }
}
