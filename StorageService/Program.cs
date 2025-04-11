using Azure.Monitor.OpenTelemetry.AspNetCore;
using LettuceEncrypt;
using Microsoft.AspNetCore.Connections;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StorageService.Components;
using StorageService.Storage;
using Yarp.ReverseProxy.Model;

Directory.CreateDirectory(Constants.StateDirectory);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("credentials.json", optional: true);

if (OperatingSystem.IsLinux())
{
    builder.WebHost.UseTunnelTransport("https://mihubot.xyz/_yarp-tunnel?host=mihubot-storage", options =>
    {
        options.AuthorizationHeaderValue = builder.Configuration["YarpTunnelAuth"];
    });
}

const int MB = 1 << 20;

builder.WebHost.UseSockets(options =>
{
    options.MaxReadBufferSize = 2 * MB;
    options.MaxWriteBufferSize = 1 * MB;
});

builder.WebHost.UseKestrel(options =>
{
    options.Limits.MaxResponseBufferSize = 2 * MB;
    options.Limits.Http2.InitialStreamWindowSize = 2 * MB;
    options.Limits.Http2.InitialConnectionWindowSize = 3 * MB;

    options.ListenAnyIP(80);

    options.ListenAnyIP(443, listenOptions =>
    {
        listenOptions.UseHttps(options =>
        {
            if (OperatingSystem.IsLinux())
            {
                options.UseLettuceEncrypt(listenOptions.ApplicationServices);
            }
        });
    });
});

if (OperatingSystem.IsLinux())
{
    DirectoryInfo certDir = new("/home/certs");
    certDir.Create();

    builder.Services.AddLettuceEncrypt()
        .PersistDataToDirectory(certDir, "certpass123");
}

if (OperatingSystem.IsLinux())
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor(options =>
        {
            options.ConnectionString = builder.Configuration["AzureMonitorConnectionString"];
        })
        .ConfigureResource(builder =>
        {
            builder.AddAttributes(new Dictionary<string, object>
            {
                { "service.name", "storage" },
                { "service.namespace", "mihubot" },
                { "service.instance.id", "storage" },
                { "service.version", Helpers.GetCommitId() }
            });
        })
        .WithTracing(builder =>
        {
            builder.AddAspNetCoreInstrumentation();
            builder.AddHttpClientInstrumentation();
        })
        .WithLogging();
}

builder.Services.AddDatabases();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

builder.Services.AddStorageServices();

var app = builder.Build();

app.UseRouting();

app.Use((context, next) =>
{
    if (string.Equals(context.Request.Host.Host, "mihubot-sec-arm", StringComparison.OrdinalIgnoreCase) && context.Connection.LocalPort != 80)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Task.CompletedTask;
    }

    if (OperatingSystem.IsLinux() && !AllowList(context) && !context.IsTunnelRequest())
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    return next();

    static bool AllowList(HttpContext context)
    {
        PathString path = context.Request.Path;

        if (path.StartsWithSegments("/s") || path.StartsWithSegments("/Management"))
        {
            return true;
        }

        // Allow requests routed by YARP.
        return context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is not null;
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.MapGroup("/s").MapStorageApis();

app.UseAntiforgery();

app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapReverseProxy();

try
{
    await app.RunDatabaseMigrations();

    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        cts.Cancel();
    };

    Task hostTask = app.RunAsync(cts.Token);

    if (await Task.WhenAny(hostTask, Lifetime.StopTCS.Task) != hostTask)
    {
        cts.Cancel();
        try
        {
            await Lifetime.StopTCS.Task;
        }
        catch { }
    }

    await hostTask;
}
catch (Exception ex)
{
    Console.WriteLine(ex);
}

static class Lifetime
{
    public static readonly TaskCompletionSource StopTCS = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
