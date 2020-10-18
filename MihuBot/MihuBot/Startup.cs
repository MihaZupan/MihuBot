using AspNet.Security.OAuth.Discord;
using Discord.WebSocket;
using LettuceEncrypt;
using Microsoft.AspNetCore.Authentication.Cookies;
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

            services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect($"{Secrets.Redis.DatabaseAddress},password={Secrets.Redis.DatabasePassword}"));

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

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = DiscordAuthenticationDefaults.AuthenticationScheme;
                })
                .AddCookie()
                .AddDiscord(options =>
                {
                    options.ClientId = Secrets.Discord.ClientId;
                    options.ClientSecret = Secrets.Discord.ClientSecret;
                    options.SaveTokens = true;

                    options.Scope.Add("guilds");
                });

            services.AddHttpContextAccessor();

            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddControllers();

            services.AddAuthorization(options =>
            {
                options.AddPolicy("Admin", policy =>
                    policy.RequireAssertion(context =>
                        context.User.IsAdmin()));
            });
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

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        }
    }
}
