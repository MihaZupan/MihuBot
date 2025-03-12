using System.Reflection;
using System.Runtime.InteropServices;
using LettuceEncrypt;
using Microsoft.AspNetCore.Connections;
using StorageService.Components;
using static System.Runtime.InteropServices.JavaScript.JSType;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("credentials.json", optional: true);

builder.WebHost.UseTunnelTransport("https://mihubot.xyz/_yarp-tunnel?host=mihubot-storage", options =>
{
    options.AuthorizationHeaderValue = builder.Configuration["YarpTunnelAuth"];
});

builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(80);

    options.ListenAnyIP(443, listenOptions =>
    {
        listenOptions.UseHttps(options =>
        {
            options.UseLettuceEncrypt(listenOptions.ApplicationServices);
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
    if (!AllowList(context.Request.Path) && !context.Connection.Id.StartsWith("yarp-tunnel-", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    return next();

    static bool AllowList(PathString path) =>
        path.StartsWithSegments("/s") ||
        path.StartsWithSegments("/superpmi") ||
        path.StartsWithSegments("/.well-known");
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

app.MapGet("/version", () =>
{
    try
    {
        var assembly = typeof(Program).Assembly;
        var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        string? commit = attribute?.InformationalVersion;

        if (commit is not null)
        {
            int plusOffset = commit.IndexOf('+');
            if (plusOffset >= 0)
            {
                commit = commit.Substring(plusOffset + 1);
            }
        }

        return
            $"""
            RID: {RuntimeInformation.RuntimeIdentifier}
            Version: {RuntimeInformation.FrameworkDescription}
            Build: {(commit is null ? "local" : $"[`{commit.AsSpan(0, 6)}`](https://github.com/MihaZupan/MihuBot/commit/{commit})")}
            WorkDir: {Environment.CurrentDirectory}
            Machine: {Environment.MachineName}
            """;
    }
    catch (Exception ex)
    {
        return ex.ToString();
    }
});

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

app.Run();

static class Lifetime
{
    public static readonly TaskCompletionSource StopTCS = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
