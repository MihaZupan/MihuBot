using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using LettuceEncrypt;
using Microsoft.ApplicationInsights.Extensibility.EventCounterCollector;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using MihuBot.Audio;
using MihuBot.Configuration;
using MihuBot.Data;
using MihuBot.Location;
using MihuBot.Permissions;
using MihuBot.Reminders;
using MihuBot.RuntimeUtils;
using Octokit;
using SpotifyAPI.Web;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using Telegram.Bot;

namespace MihuBot;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        Console.WriteLine("Configuring services ...");

        services.AddDatabases();

        string devSuffix = OperatingSystem.IsLinux() ? "" : "-dev";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            DirectoryInfo certDir = new("/home/certs");
            certDir.Create();

            services.AddLettuceEncrypt()
                .PersistDataToDirectory(certDir, "certpass123");
        }

        services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);

        if (Program.AzureEnabled && OperatingSystem.IsLinux())
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

        var discord = new InitializedDiscordClient(
            new DiscordSocketConfig()
            {
                MessageCacheSize = 1024 * 16,
                ConnectionTimeout = 30_000,
                AlwaysDownloadUsers = true,
                GatewayIntents = GatewayIntents.All | GatewayIntents.GuildMembers | GatewayIntents.MessageContent,
            },
            TokenType.Bot,
            Configuration[$"Discord:AuthToken{devSuffix}"]);
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

        services.AddSingleton<OpenAIService>();

        services.AddSingleton<UrlShortenerService>();

        services.AddSingleton<ReminderService>();

        services.AddSingleton<OpenWeatherClient>();

        services.AddSingleton<LocationService>();

        services.AddSingleton<HetznerClient>();

        services.AddSingleton<GitHubNotificationsService>();

        services.AddSingleton<CoreRootService>();

        services.AddSingleton<RuntimeUtilsService>();
        services.AddHostedService(s => s.GetRequiredService<RuntimeUtilsService>());

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
            ApplicationName = $"MihuBot{devSuffix}"
        }));

        services.AddSingleton<AudioService>();

        services.AddSingleton(new TelegramBotClient(Configuration["TelegramBot:ApiKey"]));

        services.AddSingleton<TelegramService>();

        services.AddHostedService<MihuBotService>();

        services.AddCors(options =>
        {
            options.AddPolicy("noCors", policy => { });

            options.AddDefaultPolicy(policy => policy
                .AllowAnyOrigin()
                .DisallowCredentials());
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie()
            .AddDiscord(options =>
            {
                options.SaveTokens = true;
                options.ClientId = KnownUsers.MihuBot.ToString();
                options.ClientSecret = Configuration[$"Discord:ClientSecret{devSuffix}"];
                options.Scope.Add("guilds");

                options.Events.OnTicketReceived = MergeIdentities;
            })
            .AddGitHub(options =>
            {
                options.SaveTokens = true;
                options.ClientId = Configuration[$"GitHub:ClientId{devSuffix}"];
                options.ClientSecret = Configuration[$"GitHub:ClientSecret{devSuffix}"];

                options.Events.OnTicketReceived = MergeIdentities;
            });

        static async Task MergeIdentities(TicketReceivedContext context)
        {
            if (context.Principal is { } newPrincipal &&
                newPrincipal.Identities.Single() is { } newIdentity &&
                newIdentity.IsAuthenticated)
            {
                AuthenticateResult result = await context.HttpContext.AuthenticateAsync();

                if (result.Succeeded &&
                    result.Principal is { } currentPrincipal)
                {
                    foreach (ClaimsIdentity currentIdentity in currentPrincipal.Identities)
                    {
                        if (currentIdentity.IsAuthenticated &&
                            currentIdentity.AuthenticationType != newIdentity.AuthenticationType)
                        {
                            newPrincipal.AddIdentity(currentIdentity);
                        }
                    }
                }
            }
        }

        services.AddRazorPages();
        services.AddServerSideBlazor();
        services.AddControllers();

        services.AddAuthorizationBuilder()
            .AddPolicy("Admin", policy =>
                policy.RequireAssertion(context =>
                    context.User.IsAdmin()))
            .AddPolicy("Discord", policy =>
                policy.RequireAssertion(context =>
                    context.User.TryGetDiscordUserId(out _)))
            .AddPolicy("GitHub", policy =>
                policy.RequireAssertion(context =>
                    context.User.TryGetGitHubLogin(out _)));

        services.AddTunnelServices();

        services.AddReverseProxy()
            .LoadFromConfig(Configuration.GetSection("ReverseProxy"));

        Console.WriteLine("Services configured.");
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        Console.WriteLine("Configuring app ...");

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            const string DebugHash = "9C671B9323A2CC35CC181B9894CA9EF6BA742BE0E9BD9719BB339A9607A4749C";

            static bool IsDebugMode(HttpContext context) =>
                context.User.IsAdmin() ||
                (context.Request.Query.TryGetValue("debug", out var value) &&
                ManagementController.CheckToken(DebugHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))));

            app.UseWhen(IsDebugMode,
                app => app.UseDeveloperExceptionPage());

            app.UseWhen(ctx => !IsDebugMode(ctx),
                app => app.UseExceptionHandler("/Error"));
        }

        if (env.IsProduction())
        {
            app.UseHsts();
        }

        app.UseMiddleware<IPLoggerMiddleware>();

        app.UseHttpLogging();

        app.UseCors();

        app.UseWhen(context => !(context.Request.Path.HasValue && context.Request.Path.Value.Contains("/api/", StringComparison.OrdinalIgnoreCase)),
            app => app.UseHttpsRedirection());

        app.UseAntiforgery();

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

            endpoints.MapHttp2Tunnel("/_yarp-tunnel")
                .Add(ConfigureYarpTunnelAuth);

            endpoints.MapReverseProxy();
        });

        Console.WriteLine("App configured.");
    }

    private static void ConfigureYarpTunnelAuth(EndpointBuilder builder)
    {
        var next = builder.RequestDelegate;

        builder.RequestDelegate = context =>
        {
            var config = context.RequestServices.GetRequiredService<IConfigurationService>();

            if (!context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var token) || token.Count != 1 ||
                !context.Request.Query.TryGetValue("host", out var host) || host.Count != 1 ||
                !config.TryGet(null, $"YarpTunnelAuth.{host}", out string expectedAuthorization) ||
                !ManagementController.CheckToken(expectedAuthorization, token.ToString()))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            }
            else if (next is not null)
            {
                return next(context);
            }

            return Task.CompletedTask;
        };
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
