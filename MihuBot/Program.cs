using System.Security.Claims;
using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.Net.Http.Headers;
using MihuBot;
using MihuBot.Components;
using MihuBot.Configuration;
using MihuBot.Discord;
using MihuBot.Discord.Audio;
using MihuBot.Discord.Location;
using MihuBot.Discord.Permissions;
using MihuBot.Discord.Reminders;
using MihuBot.Helpers.AI;
using MihuBot.Helpers.Cloud;
using MihuBot.Helpers.Crypto;
using MihuBot.Helpers.Diagnostics;
using MihuBot.Helpers.Torrent;
using MihuBot.Molly;
using MihuBot.RuntimeUtils;
using MihuBot.RuntimeUtils.AI;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using MihuBot.RuntimeUtils.Search;
using MihuBot.Storage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qdrant.Client;
using SpotifyAPI.Web;
using Telegram.Bot;
using Yarp.ReverseProxy.Configuration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(10));

Console.WriteLine("Starting ...");

//Console.WriteLine("TEMP: Waiting before starting ..."); while (Console.ReadLine() != "start") { }

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
    // Allows supplying config (e.g. an Azure service principal) when running outside of Azure.
    builder.Configuration.AddJsonFile("credentials.json", optional: true);

    if (ProgramState.AzureEnabled)
    {
        ProgramState.AzureCredential = CreateAzureCredential(builder.Configuration);

        builder.Configuration.AddAzureKeyVault(
            new Uri("https://mihubotkv.vault.azure.net/"),
            ProgramState.AzureCredential);
    }

    // Discord is the one integration MihuBot can't run without.
    if (!builder.Configuration.IsConfigured(OptionalFeatures.Discord))
    {
        Console.WriteLine(
            $"""
            Discord is not configured, MihuBot can not start.
            Set '{OptionalFeatures.Discord.Keys[0]}' (the bot token) in Azure Key Vault, in credentials.json next to the executable,
            or via the '{OptionalFeatures.Discord.Keys[0].Replace(":", "__")}' environment variable.
            """);
        return;
    }

    builder.WebHost.UseKestrel(options =>
    {
        options.Limits.MaxResponseBufferSize *= 32;
        options.Limits.Http2.InitialStreamWindowSize *= 32;
        options.Limits.Http2.InitialConnectionWindowSize *= 32;

        options.ConfigureEndpointDefaults(options =>
        {
            ILogger<Program> logger = options.ApplicationServices.GetRequiredService<ILogger<Program>>();

            options.Use(next => context =>
            {
                logger.LogInformation("Connection {ConnectionId} from {RemoteIP} to {LocalPort}",
                    context.ConnectionId, context.RemoteEndPoint, (context.LocalEndPoint as IPEndPoint)?.Port);

                return next(context);
            });
        });

        options.ListenAnyIP(5000);
        options.ListenAnyIP(5001, options => options.Protocols = HttpProtocols.Http2); // H2C
    });

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

    _ = Task.Run(async () =>
    {
        // Post-startup cleanup
        await Task.Delay(TimeSpan.FromSeconds(30));
        GC.Collect();
    });

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

static TokenCredential CreateAzureCredential(IConfiguration configuration)
{
    // A service principal can authenticate to Azure from anywhere, so prefer it when configured.
    string tenantId = configuration["Azure:TenantId"];
    string clientId = configuration["Azure:ClientId"];
    string clientSecret = configuration["Azure:ClientSecret"];

    if (!string.IsNullOrEmpty(tenantId) &&
        !string.IsNullOrEmpty(clientId) &&
        !string.IsNullOrEmpty(clientSecret))
    {
        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    if (!OperatingSystem.IsLinux())
    {
        return new AzureCliCredential();
    }

    // Uses the managed identity when inside Azure, and falls back to other credentials elsewhere.
    return new DefaultAzureCredential();
}

static void ConfigureServices(WebApplicationBuilder builder, IServiceCollection services)
{
    services.AddDatabases(builder.Configuration);

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

    string devSuffix = Constants.DevSuffix;

    if (ProgramState.AzureEnabled && OperatingSystem.IsLinux() &&
        builder.Configuration.IsConfigured(OptionalFeatures.AppInsights))
    {
        builder.Services.AddOpenTelemetry()
            .UseAzureMonitor(options =>
            {
                options.ConnectionString = builder.Configuration["AppInsights:ConnectionString"];
            })
            .ConfigureResource(builder =>
            {
                builder.AddAttributes(new Dictionary<string, object>
                {
                    { "service.name", "mihubot" },
                    { "service.namespace", "mihubot" },
                    { "service.instance.id", "mihubot" },
                    { "service.version", BuildInfo.GetCommitId() }
                });
            })
            .WithTracing(builder =>
            {
                builder.AddAspNetCoreInstrumentation();
                builder.AddHttpClientInstrumentation();
                builder.AddSource("Yarp.ReverseProxy");
            })
            .WithLogging()
            .WithMetrics(m =>
            {
                m.AddView("http.client.open_connections", new MetricStreamConfiguration() { TagKeys = ["network.protocol.version"] });
                m.AddView("http.client.active_requests", new MetricStreamConfiguration() { TagKeys = ["network.protocol.version"] });
                m.AddView("http.client.request.time_in_queue", new MetricStreamConfiguration() { TagKeys = ["network.protocol.version"] });
                m.AddView("http.client.connection.duration", new MetricStreamConfiguration() { TagKeys = ["network.protocol.version"] });
                m.AddView("http.client.request.duration", new MetricStreamConfiguration() { TagKeys = ["network.protocol.version"] });

                m.AddView("http.server.request.duration", new MetricStreamConfiguration() { TagKeys = ["network.protocol.version"] });
                m.AddView("http.server.active_requests", new MetricStreamConfiguration() { TagKeys = ["network.protocol.version"] });
            });
    }

    services.AddHttpLogging(logging =>
    {
        logging.RequestHeaders.Add(HeaderNames.Referer);
        logging.RequestHeaders.Add(HeaderNames.Origin);
    });

    services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.All;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var httpClient = new HttpClient(new HttpClientHandler()
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        DefaultRequestVersion = HttpVersion.Version20
    };
    services.AddSingleton(httpClient);

    builder.AddGitHubDataIngestion();

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

    services.AddSingleton<ServiceConfiguration>();

    services.AddSingleton<AvailableFeatures>();

    services.AddHostedService<OptionalFeatureReportService>();

    services.AddSingleton<Logger>();

    services.TryAddEnumerable(
        ServiceDescriptor.Singleton<ILoggerProvider, LoggerAdapterLoggerProvider>());

    services.AddSingleton<IPermissionsService, PermissionsService>();

    services.AddSingleton<SystemUsageService>();
    services.AddHostedService(s => s.GetRequiredService<SystemUsageService>());

    // Everything AI-related needs at least the primary AzureOpenAI endpoint.
    bool openAIEnabled = builder.Configuration.IsConfigured(OptionalFeatures.AzureOpenAI);

    bool gitHubEnabled = builder.Configuration.IsConfigured(OptionalFeatures.GitHub);
    bool gitHubDbEnabled = builder.Configuration.IsConfigured(OptionalFeatures.GitHubDatabase);

    // Runtime-utils jobs need both the GitHub API and the ingested GitHub data.
    bool runtimeUtilsEnabled = gitHubEnabled && gitHubDbEnabled;

    // Search and triage run on top of the ingested GitHub data.
    bool gitHubAIEnabled = openAIEnabled && runtimeUtilsEnabled;

    if (openAIEnabled)
    {
        services.AddSingleton<OpenAIService>();
    }

    services.AddSingleton<UrlShortenerService>();

    services.AddSingleton<ReminderService>();

    if (builder.Configuration.IsConfigured(OptionalFeatures.OpenWeather))
    {
        services.AddSingleton<OpenWeatherClient>();

        if (openAIEnabled)
        {
            services.AddSingleton<LocationService>();
        }
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.Hetzner))
    {
        services.AddSingleton<HetznerClient>();
    }

    services.AddStorageServices();

    if (builder.Configuration.IsConfigured(OptionalFeatures.Molly))
    {
        services.AddMollyServices();
    }

    if (gitHubEnabled)
    {
        services.AddSingleton<CoreRootService>();
        services.AddHostedService(s => s.GetRequiredService<CoreRootService>());
    }

    services.AddSingleton<RegexSourceGenerator>();

    if (runtimeUtilsEnabled)
    {
        services.AddSingleton<GitHubNotificationsService>();

        services.AddSingleton<HelixAvailabilityService>();
        services.AddHostedService(s => s.GetRequiredService<HelixAvailabilityService>());
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.Qdrant))
    {
        builder.Services.AddSingleton(new QdrantClient(builder.Configuration["Qdrant:Host"], int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334")));
        builder.Services.AddQdrantVectorStore();
    }

    if (gitHubAIEnabled)
    {
        services.AddSingleton<GitHubSearchService>();

        services.AddSingleton<IssueTriageHelper>();

        services.AddSingleton<IssueTriageService>();
        services.AddHostedService(s => s.GetRequiredService<IssueTriageService>());
    }

    if (runtimeUtilsEnabled)
    {
        services.AddSingleton<RuntimeUtilsService>();
        services.AddHostedService(s => s.GetRequiredService<RuntimeUtilsService>());
    }

    if (gitHubAIEnabled)
    {
        services.AddSingleton<DetectIssueAreaLabelsService>();
        services.AddHostedService(s => s.GetRequiredService<DetectIssueAreaLabelsService>());
    }

    if (gitHubEnabled)
    {
        services.AddSingleton<SelfUpdateService>();
        services.AddHostedService(s => s.GetRequiredService<SelfUpdateService>());
    }

    if (gitHubAIEnabled)
    {
        services.AddSingleton<McpServer>();
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.Minecraft))
    {
        services.AddSingleton(new MinecraftRCON(builder.Configuration["Minecraft:Host"], int.Parse(builder.Configuration["Minecraft:Port"] ?? "25575"), builder.Configuration["Minecraft:RconPassword"]));
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.QBittorrent))
    {
        services.AddSingleton(new QBittorrentClient(builder.Configuration["QBittorrent:Host"], builder.Configuration["QBittorrent:Username"], builder.Configuration["QBittorrent:Password"]));
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.Jellyfin))
    {
        services.AddSingleton(new JellyfinClient(builder.Configuration["Jellyfin:Host"], builder.Configuration["Jellyfin:ApiKey"]));
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.Spotify))
    {
        services.AddSingleton(new SpotifyClient(SpotifyClientConfig.CreateDefault()
            .WithAuthenticator(new ClientCredentialsAuthenticator(
                builder.Configuration["Spotify:ClientId"],
                builder.Configuration["Spotify:ClientSecret"]))));
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.Youtube))
    {
        services.AddSingleton(new YouTubeService(new BaseClientService.Initializer()
        {
            ApiKey = builder.Configuration["Youtube:ApiKey"],
            ApplicationName = $"MihuBot{devSuffix}"
        }));
    }

    services.AddSingleton<AudioService>();

    if (builder.Configuration.IsConfigured(OptionalFeatures.Telegram))
    {
        services.AddSingleton(new TelegramBotClient(builder.Configuration["TelegramBot:ApiKey"]));

        services.AddSingleton<TelegramService>();
    }

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
        });

    // OAuth handlers validate their options on every request, so they can only be registered when configured.
    if (builder.Configuration.IsConfigured(OptionalFeatures.DiscordOAuth))
    {
        services.AddAuthentication()
            .AddDiscord(options =>
            {
                options.SaveTokens = true;
                options.ClientId = KnownUsers.MihuBot.ToString();
                options.ClientSecret = builder.Configuration[$"Discord:ClientSecret{devSuffix}"];
                options.Scope.Add("guilds");

                options.Events.OnTicketReceived = MergeIdentities;
            });
    }

    if (builder.Configuration.IsConfigured(OptionalFeatures.GitHubOAuth))
    {
        services.AddAuthentication()
            .AddGitHub(options =>
            {
                options.SaveTokens = true;
                options.ClientId = builder.Configuration[$"GitHub:ClientId{devSuffix}"];
                options.ClientSecret = builder.Configuration[$"GitHub:ClientSecret{devSuffix}"];

                options.Events.OnTicketReceived = MergeIdentities;
            });
    }

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
    services.AddRemoveUnavailableControllersConvention();

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

    if (gitHubAIEnabled)
    {
        services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpServer>();
    }
}

static void Configure(WebApplication app, IWebHostEnvironment env)
{
    app.UseForwardedHeaders();

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
            CryptographicOperations.FixedTimeEquals(DebugHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))));

        app.UseWhen(IsDebugMode,
            app => app.UseDeveloperExceptionPage());

        app.UseWhen(ctx => !IsDebugMode(ctx),
            app => app.UseExceptionHandler("/Error"));
    }

    if (env.IsProduction())
    {
        app.UseHsts();
    }

    app.UseHttpLogging();

    app.UseCors();

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

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.MapTunnel("/_yarp-tunnel")
        .Add(ConfigureYarpTunnelAuth);

    app.MapGroup("/s").MapStorageApis();

    if (app.Services.GetService<MollyService>() is not null)
    {
        app.MapGroup("/api/molly").MapMollyApis();
    }

    app.MapReverseProxy();

    if (app.Services.GetService<McpServer>() is not null)
    {
        app.MapMcp("/mcp");
    }
}

static void ConfigureYarpTunnelAuth(EndpointBuilder builder)
{
    RequestDelegate next = builder.RequestDelegate;

    builder.RequestDelegate = context =>
    {
        var config = context.RequestServices.GetRequiredService<IConfigurationService>();

        if (!context.Request.Query.TryGetValue("host", out var host) || host.Count != 1 ||
            !config.TryGet(null, $"YarpTunnelAuth.{host}", out string expectedAuthorization) ||
            !TokenHelper.CheckToken(context.Request.Headers, HeaderNames.Authorization, expectedAuthorization))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    };
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
