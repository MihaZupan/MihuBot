using AspNet.Security.OAuth.Discord;
using Azure;
using Azure.AI.TextAnalytics;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using InstagramApiSharp.API;
using InstagramApiSharp.API.Builder;
using InstagramApiSharp.Classes;
using LettuceEncrypt;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using MihuBot.Audio;
using MihuBot.Configuration;
using MihuBot.Email;
using MihuBot.Husbando;
using MihuBot.NextCloud;
using MihuBot.Permissions;
using MihuBot.Reminders;
using MihuBot.Weather;
using SpotifyAPI.Web;
using System.Runtime.InteropServices;
using Tweetinvi;
using Tweetinvi.Models;

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
                DirectoryInfo certDir = new("/home/certs");
                certDir.Create();

                services.AddLettuceEncrypt()
                    .PersistDataToDirectory(certDir, "certpass123");
            }

            var httpClient = new HttpClient(new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.All
            });
            services.AddSingleton(httpClient);

            var nextCloudClient = new NextCloudClient(httpClient,
                Configuration["NextCloud:Server"],
                Configuration["NextCloud:User"],
                Configuration["NextCloud:Password"]);
            services.AddSingleton(nextCloudClient);

            services.AddSingleton<ITwitterClient>(new TwitterClient(new TwitterCredentials(
                Configuration["Twitter:ConsumerKey"],
                Configuration["Twitter:ConsumerSecret"],
                Configuration["Twitter:AccessToken"],
                Configuration["Twitter:AccessTokenSecret"])
            {
                BearerToken = Configuration["Twitter:BearerToken"]
            }));

            services.AddSingleton<IComputerVisionClient>(new ComputerVisionClient(
                new ApiKeyServiceClientCredentials(Configuration["AzureComputerVision:SubscriptionKey"]),
                httpClient,
                disposeHttpClient: false)
            {
                Endpoint = Configuration["AzureComputerVision:Endpoint"]
            });

            services.AddSingleton(new TextAnalyticsClient(
                new Uri(Configuration["AzureTextAnalytics:Endpoint"], UriKind.Absolute),
                new AzureKeyCredential(Configuration["AzureTextAnalytics:SubscriptionKey"])));

            var discord = new InitializedDiscordClient(
                new DiscordSocketConfig()
                {
                    MessageCacheSize = 1024 * 16,
                    ConnectionTimeout = 30_000,
                    AlwaysDownloadUsers = true,
                    GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers,
                },
                TokenType.Bot,
#if DEBUG
                Configuration["Discord:AuthToken-Dev"]
#else
                Configuration["Discord:AuthToken"]
#endif
                );
            services.AddSingleton(discord);
            services.AddSingleton<DiscordSocketClient>(discord);

            services.AddSingleton(new LoggerOptions(
                discord,
                $"{Constants.StateDirectory}/logs", string.Empty,
                Channels.Debug,
                Channels.LogText,
                Channels.Files));

            services.AddSingleton<Logger>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                AddPrivateDiscordClient(services, httpClient, nextCloudClient);

                services.AddHostedService<TwitterBioUpdater>();

                TryAddInstagramClient(services);
            }

            services.AddSingleton<IPermissionsService, PermissionsService>();

            services.AddSingleton<IConfigurationService, ConfigurationService>();

            services.AddSingleton<IEmailService, EmailService>();

            services.AddSingleton<IReminderService, ReminderService>();

            services.AddSingleton<IHusbandoService, HusbandoService>();

            services.AddSingleton<IWeatherService, WeatherService>();

            AddDownBadProviders(services);

            services.AddSingleton(new SpotifyClient(SpotifyClientConfig.CreateDefault()
                .WithAuthenticator(new ClientCredentialsAuthenticator(
                    Configuration["Spotify:ClientId"],
                    Configuration["Spotify:ClientSecret"]))));

            services.AddSingleton(new YouTubeService(new BaseClientService.Initializer()
            {
                ApiKey = Configuration["Youtube:ApiKey"],
                ApplicationName = $"MihuBot{(Debugger.IsAttached ? "-dev" : "")}"
            }));

            services.AddSingleton<AudioService>();

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
                    options.ClientId = KnownUsers.MihuBot.ToString();
#if DEBUG
                    options.ClientSecret = Configuration["Discord:ClientSecret-Dev"];
#else
                    options.ClientSecret = Configuration["Discord:ClientSecret"];
#endif
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

        private void AddDownBadProviders(IServiceCollection services)
        {
            services.AddSingleton<DownBadProviders.IDownBadProvider, DownBadProviders.TwitterProvider>();

            if (services.Any(s => s.ServiceType == typeof(IInstaApi)))
            {
                services.AddSingleton<DownBadProviders.IDownBadProvider, DownBadProviders.InstagramProvider>();
            }
        }

        private void AddPrivateDiscordClient(IServiceCollection services, HttpClient httpClient, NextCloudClient nextCloudClient)
        {
            var privateDiscordClient = new InitializedDiscordClient(
                    new DiscordSocketConfig()
                    {
                        MessageCacheSize = 1024, // Is this needed?
                        ConnectionTimeout = 30_000,
                        AlwaysDownloadUsers = false,
                        GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers,
                    },
                    /* TokenType.User */ 0,
                    Configuration["Discord:PrivateAuthToken"]);

            var customLogger = new CustomLogger(httpClient,
                new LoggerOptions(
                    privateDiscordClient,
                    $"{Constants.StateDirectory}/pvt_logs", "Private_",
                    Channels.Debug,
                    806049221631410186ul,
                    Channels.Files)
                {
                    ShouldLogAttachments = static message =>
                    {
                        if (message.Guild()?.GetUser(KnownUsers.MihuBot) is not SocketGuildUser user)
                            return true;

                        if (message.Channel is not SocketTextChannel channel)
                            return true;

                        return !user.GetPermissions(channel).ViewChannel;
                    }
                },
                nextCloudClient,
                Configuration);

            services.AddSingleton(customLogger);
            services.AddHostedService(_ => customLogger);
        }

        private bool TryAddInstagramClient(IServiceCollection services)
        {
            try
            {
                var userSession = new UserSessionData
                {
                    UserName = Configuration["Instagram:Username"],
                    Password = Configuration["Instagram:Password"]
                };

                IRequestDelay delay = RequestDelay.FromSeconds(2, 3);

                IInstaApi instaApi = InstaApiBuilder.CreateBuilder()
                    .SetUser(userSession)
                    .SetRequestDelay(delay)
                    .Build();

                string stateFile = $"{Constants.StateDirectory}/InstagramState.bin";
                if (File.Exists(stateFile))
                {
                    instaApi.LoadStateDataFromString(File.ReadAllText(stateFile));
                }

                if (!instaApi.IsUserAuthenticated)
                {
                    instaApi.SendRequestsBeforeLoginAsync().GetAwaiter().GetResult();

                    Thread.Sleep(5_000);

                    var logInResult = instaApi.LoginAsync().GetAwaiter().GetResult();

                    if (!logInResult.Succeeded)
                    {
                        Console.WriteLine($"Unable to login: {logInResult.Info.Message}");
                        return false;
                    }
                }

                File.WriteAllText(stateFile, instaApi.GetStateDataAsString());

                if (!instaApi.GetCurrentUserAsync().GetAwaiter().GetResult().Succeeded)
                {
                    return false;
                }

                services.AddSingleton(instaApi);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return false;
            }
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

            app.Use(async (context, next) =>
            {
                Console.WriteLine($"{context.Connection.RemoteIpAddress}");

                await next();
            });

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        }
    }
}
