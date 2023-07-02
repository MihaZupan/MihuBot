using AspNet.Security.OAuth.Discord;
using Azure;
using Azure.AI.TextAnalytics;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using LettuceEncrypt;
using Microsoft.ApplicationInsights.Extensibility.EventCounterCollector;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using MihuBot.Audio;
using MihuBot.Configuration;
using MihuBot.Permissions;
using MihuBot.Reminders;
using MihuBot.Weather;
using Octokit;
using SpotifyAPI.Web;
using System.Runtime.InteropServices;
using Tweetinvi;
using Tweetinvi.Models;

namespace MihuBot;

public class Startup
{
    public const GatewayIntents Intents = GatewayIntents.All | ((GatewayIntents)(1 << 15));

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        Console.WriteLine("Configuring services ...");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            DirectoryInfo certDir = new("/home/certs");
            certDir.Create();

            services.AddLettuceEncrypt()
                .PersistDataToDirectory(certDir, "certpass123");
        }

        services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);

        if (Program.AzureEnabled)
        {
            services.AddApplicationInsightsTelemetry(options =>
            {
                options.ConnectionString = Configuration["AppInsights:ConnectionString"] ?? throw new Exception("Missing AppInsights ConnectionString");
            });

            services.ConfigureTelemetryModule<EventCounterCollectionModule>((module, options) =>
            {
                foreach (var (eventSource, counters) in RuntimeEventCounters.EventCounters)
                {
                    foreach (string counter in counters)
                    {
                        module.Counters.Add(new EventCounterCollectionRequest(eventSource, counter));
                    }
                }
            });
        }

        services.AddHttpLogging(logging =>
        {
            logging.RequestHeaders.Add(HeaderNames.Referer);
            logging.RequestHeaders.Add(HeaderNames.Origin);
        });

        services.AddSingleton<IPLoggerMiddleware>();

        var httpClient = new HttpClient(new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            DefaultRequestVersion = HttpVersion.Version20
        };
        services.AddSingleton(httpClient);

        services.AddSingleton(new GitHubClient(new ProductHeaderValue("MihuBot"))
        {
            Credentials = new Credentials(Configuration["GitHub:Token"]),
        });

        if (Program.AzureEnabled)
        {
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
        }

        var discord = new InitializedDiscordClient(
            new DiscordSocketConfig()
            {
                MessageCacheSize = 1024 * 16,
                ConnectionTimeout = 30_000,
                AlwaysDownloadUsers = true,
                GatewayIntents = Intents,
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

        services.AddSingleton<IPermissionsService, PermissionsService>();

        services.AddSingleton<IConfigurationService, ConfigurationService>();

        services.AddSingleton<IReminderService, ReminderService>();

        services.AddSingleton<IWeatherService, WeatherService>();

        services.AddSingleton<RuntimeUtilsService>();

        services.AddSingleton(new MinecraftRCON("mihubot.xyz", 25575, Configuration["Minecraft:RconPassword"]));

        if (Program.AzureEnabled)
        {
            services.AddSingleton(new SpotifyClient(SpotifyClientConfig.CreateDefault()
                .WithAuthenticator(new ClientCredentialsAuthenticator(
                    Configuration["Spotify:ClientId"],
                    Configuration["Spotify:ClientSecret"]))));
        }

        services.AddSingleton(new YouTubeService(new BaseClientService.Initializer()
        {
            ApiKey = Configuration["Youtube:ApiKey"],
            ApplicationName = $"MihuBot{(Debugger.IsAttached ? "-dev" : "")}"
        }));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Program.AzureEnabled)
        {
            //AddPrivateDiscordClients(services, httpClient);

            services.AddHostedService<TwitterBioUpdater>();
        }

        services.AddSingleton<AudioService>();

        services.AddHostedService<MihuBotService>();

        services.AddCors(options =>
        {
            options.AddPolicy("noCors", policy => { });

            options.AddDefaultPolicy(policy => policy
                .AllowAnyOrigin()
                .DisallowCredentials());
        });

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

        Console.WriteLine("Services configured.");
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

        app.UseMiddleware<IPLoggerMiddleware>();

        app.UseHttpLogging();

        app.UseCors();

        app.UseWhen(context => !(context.Request.Path.HasValue && context.Request.Path.Value.Contains("/api/", StringComparison.OrdinalIgnoreCase)),
            app => app.UseHttpsRedirection());

        app.UseStaticFiles();

        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                if (Path.GetExtension(ctx.File.Name.AsSpan()) is ".png" or ".webp" or ".jpg" or ".mp4" or ".jfif")
                {
                    ctx.Context.Response.Headers.CacheControl = "public,max-age=604800";
                }
            }
        });

        app.UseRouting();

        app.UseCors();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapBlazorHub();
            endpoints.MapFallbackToPage("/_Host");
        });
    }

    private sealed class IPLoggerMiddleware : IMiddleware
    {
        private readonly ILogger<IPLoggerMiddleware> _logger;

        public IPLoggerMiddleware(ILogger<IPLoggerMiddleware> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var connection = context.Connection;
            _logger.LogInformation("Request on connection {ConnectionId} from {RemoteIP} to {LocalPort}",
                connection.Id, connection.RemoteIpAddress, connection.LocalPort);

            return next(context);
        }
    }
}
