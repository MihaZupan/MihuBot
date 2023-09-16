using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using LettuceEncrypt;
using Microsoft.ApplicationInsights.Extensibility.EventCounterCollector;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using MihuBot.Configuration;
using MihuBot.Permissions;
using MihuBot.Reminders;
using MihuBot.RuntimeUtils;
using MihuBot.Weather;
using Octokit;
using System.Runtime.InteropServices;
using System.Security.Claims;
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
            services.AddSingleton<IComputerVisionClient>(new ComputerVisionClient(
                new ApiKeyServiceClientCredentials(Configuration["AzureComputerVision:SubscriptionKey"]),
                httpClient,
                disposeHttpClient: false)
            {
                Endpoint = Configuration["AzureComputerVision:Endpoint"]
            });
        }

        var discord = new InitializedDiscordClient(
            new DiscordSocketConfig()
            {
                MessageCacheSize = 1024 * 16,
                ConnectionTimeout = 30_000,
                AlwaysDownloadUsers = true,
                GatewayIntents = GatewayIntents.All | GatewayIntents.GuildMembers | GatewayIntents.MessageContent,
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

        services.AddSingleton<HetznerClient>();

        services.AddSingleton<RuntimeUtilsService>();

        services.AddSingleton(new MinecraftRCON("mihubot.xyz", 25575, Configuration["Minecraft:RconPassword"]));

        services.AddSingleton(new YouTubeService(new BaseClientService.Initializer()
        {
            ApiKey = Configuration["Youtube:ApiKey"],
            ApplicationName = $"MihuBot{(Debugger.IsAttached ? "-dev" : "")}"
        }));

        services.AddSingleton(new TelegramBotClient(Configuration["TelegramBot:ApiKey"]));

        services.AddHostedService<MihuBotService>();

        services.AddHostedService<TelegramService>();

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
#if DEBUG
                options.ClientSecret = Configuration["Discord:ClientSecret-Dev"];
#else
                options.ClientSecret = Configuration["Discord:ClientSecret"];
#endif

                options.Scope.Add("guilds");

                options.Events.OnTicketReceived = MergeIdentities;
            })
            .AddGitHub(options =>
            {
                options.SaveTokens = true;
#if DEBUG
                options.ClientId = Configuration["GitHub:ClientId-Dev"];
                options.ClientSecret = Configuration["GitHub:ClientSecret-Dev"];
#else
                options.ClientId = Configuration["GitHub:ClientId"];
                options.ClientSecret = Configuration["GitHub:ClientSecret"];
#endif

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

            app.UseWhen(context => context.User.IsAdmin(),
                app => app.UseDeveloperExceptionPage());
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
