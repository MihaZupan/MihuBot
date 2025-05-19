using System.Security.Claims;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using LettuceEncrypt;
using Microsoft.ApplicationInsights.Extensibility.EventCounterCollector;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.Net.Http.Headers;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using MihuBot;
using MihuBot.Audio;
using MihuBot.Components;
using MihuBot.Configuration;
using MihuBot.Data;
using MihuBot.Location;
using MihuBot.Permissions;
using MihuBot.Reminders;
using MihuBot.RuntimeUtils;
using Octokit;
using Qdrant.Client;
using SpotifyAPI.Web;
using Telegram.Bot;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(10));

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
    if (ProgramState.AzureEnabled)
    {
        ProgramState.AzureCredential = OperatingSystem.IsLinux()
            ? new ManagedIdentityCredential()
            : new AzureCliCredential();

        builder.Configuration.AddAzureKeyVault(
            new Uri("https://mihubotkv.vault.azure.net/"),
            ProgramState.AzureCredential);
    }
    else
    {
        builder.Configuration.AddJsonFile("credentials.json", optional: true);
    }

    builder.WebHost.UseKestrel(options =>
    {
        options.Limits.MaxResponseBufferSize *= 32;
        options.Limits.Http2.InitialStreamWindowSize *= 32;
        options.Limits.Http2.InitialConnectionWindowSize *= 32;
    });

    builder.WebHost.UseUrls("http://*:80", "https://*:443");

    Console.WriteLine("Configuring services ...");
    ConfigureServices(builder, builder.Services);
    Console.WriteLine("Services configured.");

    WebApplication app = builder.Build();

    Console.WriteLine("Configuring app ...");
    Configure(app, app.Environment);
    Console.WriteLine("App configured.");

    await app.RunDatabaseMigrations();

    Console.WriteLine("Starting host.RunAsync ...");

    Task hostTask = app.RunAsync(cts.Token);

    if (await Task.WhenAny(hostTask, ProgramState.BotStopTCS.Task) != hostTask)
    {
        cts.Cancel();
        try
        {
            await ProgramState.BotStopTCS.Task;
        }
        catch { }
    }

    await hostTask;
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

static void ConfigureServices(WebApplicationBuilder builder, IServiceCollection services)
{
    services.AddDatabases();

    services.AddMemoryCache(options =>
    {
        options.SizeLimit = 1024 * 1024 * 1024; // 1 GB
    });

    services.AddHybridCache(options =>
    {
        options.MaximumKeyLength = 10 * 1024;

        options.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(12),
            LocalCacheExpiration = TimeSpan.FromHours(12),
        };
    });

    string devSuffix = OperatingSystem.IsLinux() ? "" : "-dev";

    if (OperatingSystem.IsLinux())
    {
        DirectoryInfo certDir = new("/home/certs");
        certDir.Create();

        services.AddLettuceEncrypt()
            .PersistDataToDirectory(certDir, "certpass123");
    }

    services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);

    if (ProgramState.AzureEnabled && OperatingSystem.IsLinux())
    {
        services.AddApplicationInsightsTelemetry(options =>
        {
            options.ConnectionString = builder.Configuration["AppInsights:ConnectionString"] ?? throw new Exception("Missing AppInsights ConnectionString"); ;
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
        Credentials = new Credentials(builder.Configuration["GitHub:Token"]),
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
        builder.Configuration[$"Discord:AuthToken{devSuffix}"]);
    services.AddSingleton(discord);
    services.AddSingleton<DiscordSocketClient>(discord);

    services.AddSingleton(new LoggerOptions(
        discord,
        $"{Constants.StateDirectory}/logs", string.Empty,
        Channels.Debug,
        Channels.LogText,
        Channels.Files));

    services.AddSingleton<IConfigurationService, ConfigurationService>();

    services.AddSingleton<Logger>();

    services.AddSingleton<IPermissionsService, PermissionsService>();

    services.AddSingleton<OpenAIService>();

    services.AddSingleton<UrlShortenerService>();

    services.AddSingleton<ReminderService>();

    services.AddSingleton<OpenWeatherClient>();

    services.AddSingleton<LocationService>();

    services.AddSingleton<HetznerClient>();

    services.AddSingleton<CoreRootService>();

    services.AddSingleton<RegexSourceGenerator>();

    services.AddSingleton<GitHubDataService>();
    services.AddHostedService(s => s.GetRequiredService<GitHubDataService>());

    services.AddSingleton<GitHubNotificationsService>();

    builder.Services.AddSingleton(new QdrantClient(builder.Configuration["Qdrant:Host"], int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334")));
    builder.Services.AddSingleton<IVectorStore, QdrantVectorStore>();

    services.AddSingleton<GitHubSearchService>();
    services.AddHostedService(s => s.GetRequiredService<GitHubSearchService>());

    services.AddSingleton<IssueTriageHelper>();

    services.AddHostedService<IssueTriageService>();

    services.AddSingleton<RuntimeUtilsService>();
    services.AddHostedService(s => s.GetRequiredService<RuntimeUtilsService>());

    services.AddSingleton(new MinecraftRCON("mihubot.xyz", 25575, builder.Configuration["Minecraft:RconPassword"]));

    if (ProgramState.AzureEnabled)
    {
        services.AddSingleton(new SpotifyClient(SpotifyClientConfig.CreateDefault()
            .WithAuthenticator(new ClientCredentialsAuthenticator(
                builder.Configuration["Spotify:ClientId"],
                builder.Configuration["Spotify:ClientSecret"]))));
    }

    services.AddSingleton(new YouTubeService(new BaseClientService.Initializer()
    {
        ApiKey = builder.Configuration["Youtube:ApiKey"],
        ApplicationName = $"MihuBot{devSuffix}"
    }));

    services.AddSingleton<AudioService>();

    services.AddSingleton(new TelegramBotClient(builder.Configuration["TelegramBot:ApiKey"]));

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
        .AddCookie(options =>
        {
            options.LoginPath = "/Account/Login/Discord";
        })
        .AddDiscord(options =>
        {
            options.SaveTokens = true;
            options.ClientId = KnownUsers.MihuBot.ToString();
            options.ClientSecret = builder.Configuration[$"Discord:ClientSecret{devSuffix}"];
            options.Scope.Add("guilds");

            options.Events.OnTicketReceived = MergeIdentities;
        })
        .AddGitHub(options =>
        {
            options.SaveTokens = true;
            options.ClientId = builder.Configuration[$"GitHub:ClientId{devSuffix}"];
            options.ClientSecret = builder.Configuration[$"GitHub:ClientSecret{devSuffix}"];

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

    if (!OperatingSystem.IsLinux())
    {
        StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
    }

    services
        .AddCascadingAuthenticationState()
        .AddRazorComponents()
        .AddInteractiveServerComponents();

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

    services.AddSingleton<IProxyConfigFilter, YarpConfigFilter>();

    services.AddTunnelServices();

    services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
}

static void Configure(WebApplication app, IWebHostEnvironment env)
{
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

    app.UseAntiforgery();

    app.UseCors();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.MapTunnel("/_yarp-tunnel")
        .Add(ConfigureYarpTunnelAuth);

    app.Map("/superpmi/{**remainder}", (HttpContext context, [FromRoute] string remainder) =>
        Results.Redirect($"https://storage.mihubot.xyz/superpmi/{remainder}"));

    app.MapForwarder("/_appinsights-ingest/{**any}", "https://eastus2-3.in.applicationinsights.azure.com", request => request.AddPathRemovePrefix("/_appinsights-ingest"));
    app.MapForwarder("/_appinsights-ingest-live/{**any}", "https://eastus2.livediagnostics.monitor.azure.com", request => request.AddPathRemovePrefix("/_appinsights-ingest-live"));

    app.MapReverseProxy();
}

static void ConfigureYarpTunnelAuth(EndpointBuilder builder)
{
    RequestDelegate next = builder.RequestDelegate;

    builder.RequestDelegate = context =>
    {
        var config = context.RequestServices.GetRequiredService<IConfigurationService>();

        if (!context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var token) || token.Count != 1 ||
            !context.Request.Query.TryGetValue("host", out var host) || host.Count != 1 ||
            !config.TryGet(null, $"YarpTunnelAuth.{host}", out string expectedAuthorization) ||
            !ManagementController.CheckToken(expectedAuthorization, token.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    };
}

file sealed class IPLoggerMiddleware : IMiddleware
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

file sealed class YarpConfigFilter(IConfigurationService configuration) : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        if (cluster.ClusterId.StartsWith("internal.", StringComparison.Ordinal))
        {
            cluster = cluster with
            {
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DestinationConfig { Address = configuration.Get(null, $"YarpConfig.{cluster.ClusterId}") }
                }
            };
        }

        return new ValueTask<ClusterConfig>(cluster);
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig cluster, CancellationToken cancel)
    {
        return new ValueTask<RouteConfig>(route);
    }
}

namespace MihuBot
{
    public sealed class ProgramState
    {
        public static bool AzureEnabled => true;

        public static TokenCredential AzureCredential { get; set; }

        public static readonly TaskCompletionSource BotStopTCS = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
