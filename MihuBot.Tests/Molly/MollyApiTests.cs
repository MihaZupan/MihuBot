using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MihuBot.DB;
using MihuBot.Helpers;
using MihuBot.Molly;
using MihuBot.Molly.Alerts;

namespace MihuBot.Tests.Molly;

/// <summary>
/// Hosts the real Molly endpoint group over loopback HTTP.
/// </summary>
public sealed class MollyApiFixture : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"molly-api-tests-{Guid.NewGuid():N}.db");
    private WebApplication _app = null!;

    public HttpClient Client { get; private set; } = null!;

    public MollyService Service => _app.Services.GetRequiredService<MollyService>();

    public MollyIdProtector IdProtector => _app.Services.GetRequiredService<MollyIdProtector>();

    /// <summary>Recovers the real entry id from the opaque token handed to the client.</summary>
    public Guid Unprotect(string protectedId)
    {
        Assert.True(IdProtector.TryUnprotect(protectedId, out Guid id));
        return id;
    }

    public async Task InitializeAsync()
    {
        DatabaseSetupHelper.NotifyMigrationsCompleted();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // The app's appsettings.json ends up in the test output directory, so the host would otherwise
        // log to the console - including the exception behind the 500 that
        // WhenTheSizeLimitCannotBeSet_TheRequestFails deliberately provokes.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Molly:ServerKey"] = MollyTestKeys.ServerKey,
            ["Molly:AppSecret"] = MollyTestKeys.AppSecret,
        });

        builder.Services.AddPooledDbContextFactory<MollyDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));

        // Registered before AddMollyServices (which uses TryAddSingleton) so that the real app's
        // Discord Logger dependency isn't needed - without it alerts just aren't announced.
        builder.Services.AddSingleton(serviceProvider => new MollyService(
            serviceProvider.GetRequiredService<IDbContextFactory<MollyDbContext>>(),
            serviceProvider.GetRequiredService<ILogger<MollyService>>(),
            serviceProvider.GetRequiredService<MollyIdProtector>(),
            serviceProvider.GetRequiredService<IConfiguration>(),
            discordLogger: null));

        builder.Services.AddMollyServices();

        _app = builder.Build();

        var dbFactory = _app.Services.GetRequiredService<IDbContextFactory<MollyDbContext>>();
        await using (MollyDbContext db = dbFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        // Lets a test simulate a server that can't have its request body size limit set.
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(MollyApiTests.RemoveSizeLimitFeatureHeader))
            {
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(null);
            }

            await next(context);
        });

        _app.MapGroup(MollyApiTests.Group).MapMollyApis();

        await _app.StartAsync();

        string address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        Client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        try
        {
            File.Delete(_databasePath);
        }
        catch { }
    }
}

public sealed class MollyApiTests : IClassFixture<MollyApiFixture>
{
    internal const string Group = "/api/molly";

    /// <summary>Makes the test host drop <see cref="IHttpMaxRequestBodySizeFeature"/> for a request.</summary>
    internal const string RemoveSizeLimitFeatureHeader = "X-Test-Remove-Size-Limit-Feature";

    private readonly MollyApiFixture _fixture;

    public MollyApiTests(MollyApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Each test uses its own X-Real-IP so that the shared host's rate limiter
    /// doesn't leak state between tests.
    /// </summary>
    private static string NewClientIp() => $"203.0.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";

    private async Task<HttpResponseMessage> PostAsync(string path, string json, string? signature, string? clientIp = null, string? extraHeader = null)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Real-IP", clientIp ?? NewClientIp());

        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation(MollyServiceExtensions.AppSignatureHeader, signature);
        }

        if (extraHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(extraHeader, "1");
        }

        return await _fixture.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SignedPostAsync(string path, string json, string? clientIp = null) =>
        PostAsync(path, json, MollyTestKeys.Sign(path, json), clientIp);

    private static string LoginBody(string keyHash) => $$"""{"keyHash":"{{keyHash}}"}""";

    private async Task<(string Id, string ServerHmac)> RegisterAsync()
    {
        HttpResponseMessage response = await SignedPostAsync($"{Group}/login", LoginBody(MollyTestKeys.NewKeyHash()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetString()!, json.GetProperty("serverHmac").GetString()!);
    }

    [Fact]
    public async Task Login_Signed_ReturnsOpaqueTokenAndBase64ServerHmac()
    {
        (string id, string serverHmac) = await RegisterAsync();

        // The database id is never sent to the client, only a token it can hand back.
        Assert.False(Guid.TryParse(id, out _));
        Assert.True(_fixture.IdProtector.TryUnprotect(id, out Guid entryId));
        Assert.NotEqual(Guid.Empty, entryId);

        Assert.Equal(64, Convert.FromBase64String(serverHmac).Length);
    }

    [Fact]
    public async Task Login_Unsigned_IsUnauthorizedAndCreatesNoEntry()
    {
        var dbFactory = _fixture.Service;
        string keyHash = MollyTestKeys.NewKeyHash();

        HttpResponseMessage response = await PostAsync($"{Group}/login", LoginBody(keyHash), signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The same key hash must still be unknown, i.e. logging in properly registers it now.
        MollyLoginResult result = await _fixture.Service.LoginAsync(keyHash, default);
        Assert.Equal(MollyResultStatus.Ok, result.Status);
        Assert.Equal(1, await CountEntriesWithKeyHashAsync(keyHash));
    }

    private async Task<int> CountEntriesWithKeyHashAsync(string keyHash)
    {
        // Logging in twice must resolve to the same entry, proving only one was ever created.
        MollyLoginResult first = await _fixture.Service.LoginAsync(keyHash, default);
        MollyLoginResult second = await _fixture.Service.LoginAsync(keyHash, default);

        _fixture.IdProtector.TryUnprotect(first.ProtectedId, out Guid firstId);
        _fixture.IdProtector.TryUnprotect(second.ProtectedId, out Guid secondId);

        return firstId == secondId ? 1 : 2;
    }

    [Fact]
    public async Task Login_BadSignature_IsUnauthorized()
    {
        string signature = Convert.ToBase64String(new byte[64]);

        HttpResponseMessage response = await PostAsync($"{Group}/login", LoginBody(MollyTestKeys.NewKeyHash()), signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SignatureFromAnotherEndpoint_CannotBeReplayed()
    {
        string json = LoginBody(MollyTestKeys.NewKeyHash());
        string signature = MollyTestKeys.Sign($"{Group}/login", json);

        HttpResponseMessage response = await PostAsync($"{Group}/ping", json, signature);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Associate_Signed_ReturnsOk()
    {
        (string id, _) = await RegisterAsync();

        HttpResponseMessage response = await SignedPostAsync($"{Group}/associate", $$"""{"id":"{{id}}","nickname":"api-user"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Associate_TooLongNickname_IsBadRequest()
    {
        (string id, _) = await RegisterAsync();
        string nickname = new('a', MollyService.MaxNicknameLengthInBytes + 1);

        HttpResponseMessage response = await SignedPostAsync($"{Group}/associate", $$"""{"id":"{{id}}","nickname":"{{nickname}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ping_Signed_ReturnsPong()
    {
        (string id, _) = await RegisterAsync();

        HttpResponseMessage response = await SignedPostAsync($"{Group}/ping", $$"""{"id":"{{id}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pong", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("response").GetString());
    }

    [Fact]
    public async Task Ping_UnknownToken_IsBadRequest()
    {
        // A raw guid isn't a token this process issued, so it's rejected before any lookup.
        HttpResponseMessage response = await SignedPostAsync($"{Group}/ping", $$"""{"id":"{{Guid.NewGuid()}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"keyHash":null}""")]
    [InlineData("null")]        // Valid JSON that deserializes to a null request.
    [InlineData("[]")]          // Valid JSON of the wrong shape.
    [InlineData("123")]
    [InlineData("\"string\"")]
    [InlineData("{\"keyHash\":")] // Truncated.
    public async Task Login_InvalidBody_IsBadRequest(string json)
    {
        HttpResponseMessage response = await SignedPostAsync($"{Group}/login", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedBody_IsRejectedByTheServer()
    {
        string json = LoginBody(new string('A', 20_000));

        HttpResponseMessage response = await SignedPostAsync($"{Group}/login", json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task BodyAtTheSizeLimit_IsStillProcessed()
    {
        // Comfortably under the 8 KB limit, but far larger than a real request.
        string json = LoginBody(new string('A', 4_000));

        HttpResponseMessage response = await SignedPostAsync($"{Group}/login", json);

        // The key hash is too long to be valid, but the body was read and signature-checked.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheSizeLimitCannotBeSet_TheRequestFails()
    {
        string json = LoginBody(MollyTestKeys.NewKeyHash());

        // Rather than reading an unbounded body into memory, the filter has to fail closed.
        HttpResponseMessage response = await PostAsync(
            $"{Group}/login",
            json,
            MollyTestKeys.Sign($"{Group}/login", json),
            extraHeader: RemoveSizeLimitFeatureHeader);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task LockedEntry_ReceivesLockCommandInsteadOfSecrets()
    {
        (string id, _) = await RegisterAsync();
        string clientIp = NewClientIp();

        await _fixture.Service.SetLockRequestedAsync(_fixture.Unprotect(id), lockRequested: true);

        HttpResponseMessage response = await SignedPostAsync($"{Group}/ping", $$"""{"id":"{{id}}"}""", clientIp);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("lock", json.GetProperty("command").GetString());
    }

    [Fact]
    public async Task WipedEntry_ReceivesWipeCommand()
    {
        (string id, _) = await RegisterAsync();

        await _fixture.Service.RequestWipeAsync(_fixture.Unprotect(id));

        HttpResponseMessage response = await SignedPostAsync($"{Group}/ping", $$"""{"id":"{{id}}"}""");
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("wipe", json.GetProperty("command").GetString());
        Assert.False(json.TryGetProperty("serverHmac", out _));
    }

    [Fact]
    public async Task ExcessiveRequests_AreRateLimitedWithRetryAfter()
    {
        string clientIp = NewClientIp();
        string json = $$"""{"id":"{{Guid.NewGuid()}}"}""";

        HttpResponseMessage? limited = null;

        for (int i = 0; i < 60 && limited is null; i++)
        {
            HttpResponseMessage response = await SignedPostAsync($"{Group}/ping", json, clientIp);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        Assert.NotNull(limited);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task Alert_Signed_IsAccepted()
    {
        (string id, _) = await RegisterAsync();

        string json = $$$"""{"id":"{{{id}}}","type":"location","data":{"latitude":1.5,"longitude":2.5}}""";

        HttpResponseMessage response = await SignedPostAsync($"{Group}/alert", json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        MollyAlertInfo alert = Assert.Single(
            await _fixture.Service.GetRecentAlertsAsync(),
            a => a.EntryId == _fixture.Unprotect(id));

        Assert.Equal(MollyAlertType.Location, alert.Type);
        Assert.Equal("1.5, 2.5", alert.Summary);
    }

    [Fact]
    public async Task Alert_Unsigned_IsUnauthorized()
    {
        (string id, _) = await RegisterAsync();

        HttpResponseMessage response = await PostAsync($"{Group}/alert", $$"""{"id":"{{id}}"}""", signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Alert_WithAnUnknownToken_IsBadRequest()
    {
        HttpResponseMessage response = await SignedPostAsync($"{Group}/alert", $$"""{"id":"{{Guid.NewGuid()}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Alert_ThatIsTooLarge_IsBadRequest()
    {
        (string id, _) = await RegisterAsync();

        // Under the 8 KB request limit, but over the alert specific one.
        string json = $$"""{"id":"{{id}}","data":"{{new string('a', MollyService.MaxAlertLength)}}"}""";

        HttpResponseMessage response = await SignedPostAsync($"{Group}/alert", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetRequests_AreNotRouted()
    {
        HttpResponseMessage response = await _fixture.Client.GetAsync($"{Group}/ping");

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }
}
