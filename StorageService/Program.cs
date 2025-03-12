using System.Runtime.InteropServices;
using LettuceEncrypt;
using Microsoft.AspNetCore.Connections;
using StorageService.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("credentials.json", optional: true);

if (OperatingSystem.IsLinux())
{
    builder.WebHost.UseTunnelTransport("https://mihubot.xyz/_yarp-tunnel?host=mihubot-storage", options =>
    {
        options.AuthorizationHeaderValue = builder.Configuration["YarpTunnelAuth"];
    });
}

builder.WebHost.UseKestrel(options =>
{
    options.Limits.MaxResponseBufferSize *= 32;
    options.Limits.Http2.InitialStreamWindowSize *= 32;
    options.Limits.Http2.InitialConnectionWindowSize *= 32;

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

if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    DirectoryInfo certDir = new("/home/certs");
    certDir.Create();

    builder.Services.AddLettuceEncrypt()
        .PersistDataToDirectory(certDir, "certpass123");
}

builder.Services.AddHttpForwarder();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

var app = builder.Build();

app.Use((context, next) =>
{
    if (OperatingSystem.IsLinux() &&
        !AllowList(context.Request.Path) &&
        !context.Connection.Id.StartsWith("yarp-tunnel-", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    return next();

    static bool AllowList(PathString path) =>
        path.StartsWithSegments("/s") ||
        path.StartsWithSegments("/superpmi") ||
        path.StartsWithSegments("/.well-known") ||
        path.StartsWithSegments("/Management");
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapForwarder("/superpmi/{*any}", "https://clrjit2.blob.core.windows.net");

try
{
    using var cts = new CancellationTokenSource();

    Task hostTask = app.RunAsync(cts.Token);

    Task finishedTask = await Task.WhenAny(hostTask, Lifetime.StopTCS.Task);

    if (finishedTask != hostTask)
    {
        cts.Cancel();
        try
        {
            await finishedTask;
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
