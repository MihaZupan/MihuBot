using AspNet.Security.OAuth.Discord;
using Discord;
using Discord.WebSocket;
using LettuceEncrypt;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MihuBot.Configuration;
using MihuBot.Email;
using MihuBot.Helpers;
using MihuBot.Husbando;
using MihuBot.Permissions;
using MihuBot.Reminders;
using MihuBot.Weather;
using System.IO;
using System.Net;
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

            var httpClient = new HttpClient(new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.All
            });
            services.AddSingleton(httpClient);

            var discordConfig = new DiscordSocketConfig()
            {
                MessageCacheSize = 1024 * 16,
                ConnectionTimeout = 30_000
            };

            var discord = new InitializedDiscordClient(discordConfig, TokenType.Bot, Secrets.Discord.AuthToken);
            services.AddSingleton(discord);
            services.AddSingleton<DiscordSocketClient>(discord);

            services.AddSingleton(new LoggerOptions(
                discord,
                "logs", string.Empty,
                Channels.Debug,
                Channels.LogText,
                Channels.Files));

            services.AddSingleton<Logger>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var customLogger = new CustomLogger(httpClient,
                    new LoggerOptions(
                        new InitializedDiscordClient(discordConfig, /* TokenType.User */0, Secrets.Discord.PrivateAuthToken),
                        "pvt_logs", "Private_",
                        806048964021190656ul,
                        806049221631410186ul,
                        Channels.Files)
                    {
                        ShouldLogAttachments = message =>
                        {
                            if (message.Guild()?.GetUser(Secrets.Discord.BotId) is not SocketGuildUser user)
                                return true;

                            if (message.Channel is not SocketTextChannel channel)
                                return true;

                            return !user.GetPermissions(channel).ViewChannel;
                        }
                    });
                services.AddSingleton(customLogger);
                services.AddHostedService(_ => customLogger);
            }

            services.AddSingleton<StreamerSongListClient>();

            services.AddSingleton<IPermissionsService, PermissionsService>();

            services.AddSingleton<IConfigurationService, ConfigurationService>();

            services.AddSingleton<IEmailService, EmailService>();

            services.AddSingleton<IReminderService, ReminderService>();

            services.AddSingleton<IHusbandoService, HusbandoService>();

            services.AddSingleton<IWeatherService, WeatherService>();

            services.AddHostedService<MihuBotService>();

            services.AddHostedService<TwitchBotService>();

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
