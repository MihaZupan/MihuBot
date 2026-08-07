using System.Net;
using System.Net.Http.Headers;
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
/// Hosts the real Molly endpoint over loopback HTTP.
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
            ["Molly:DatabaseKey"] = MollyTestKeys.DatabaseKey,
            ["Molly:TransportPrivateKey"] = MollyTestKeys.TransportPrivateKey,
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
    private readonly MollyTestEnvelope _envelope = new();

    public MollyApiTests(MollyApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Each test uses its own X-Real-IP so that the shared host's rate limiter
    /// doesn't leak state between tests.
    /// </summary>
    private static string NewClientIp() => $"203.0.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";

    private async Task<HttpResponseMessage> PostRawAsync(byte[] body, string? clientIp = null, string? extraHeader = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var request = new HttpRequestMessage(HttpMethod.Post, Group) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Real-IP", clientIp ?? NewClientIp());

        if (extraHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(extraHeader, "1");
        }

        return await _fixture.Client.SendAsync(request);
    }

    /// <summary>Sends an encrypted request and returns the decrypted response envelope.</summary>
    private async Task<JsonElement> PostAsync(string action, string? data = null, string? clientIp = null)
    {
        byte[] body = _envelope.EncryptRequest(action, data);

        HttpResponseMessage response = await PostRawAsync(body, clientIp);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return _envelope.DecryptResponse(await response.Content.ReadAsByteArrayAsync());
    }

    private static string Status(JsonElement response) => response.GetProperty("status").GetString()!;

    private static JsonElement Data(JsonElement response) => response.GetProperty("data");

    private static string LoginData(string keyHash) => $$"""{"keyHash":"{{keyHash}}"}""";

    private async Task<(string Id, string ServerHmac)> RegisterAsync()
    {
        JsonElement response = await PostAsync("login", LoginData(MollyTestKeys.NewKeyHash()));
        Assert.Equal("ok", Status(response));

        JsonElement data = Data(response);
        return (data.GetProperty("id").GetString()!, data.GetProperty("serverHmac").GetString()!);
    }

    [Fact]
    public async Task Login_ReturnsOpaqueTokenAndBase64ServerHmac()
    {
        (string id, string serverHmac) = await RegisterAsync();

        // The database id is never sent to the client, only a token it can hand back.
        Assert.False(Guid.TryParse(id, out _));
        Assert.True(_fixture.IdProtector.TryUnprotect(id, out Guid entryId));
        Assert.NotEqual(Guid.Empty, entryId);

        Assert.Equal(64, Convert.FromBase64String(serverHmac).Length);
    }

    [Fact]
    public async Task Request_SealedToADifferentServerKey_IsBadRequest()
    {
        var wrongKey = new MollyTestEnvelope(MollyTestKeys.OtherTransportPublicKeyBytes);
        byte[] body = wrongKey.EncryptRequest("login", LoginData(MollyTestKeys.NewKeyHash()));

        HttpResponseMessage response = await PostRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_ThatIsNotEvenAnEnvelope_IsBadRequest()
    {
        HttpResponseMessage response = await PostRawAsync(new byte[8]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithAStaleTimestamp_IsBadRequest()
    {
        long stale = MollyTestEnvelope.Now() - (long)MollyRequestProtector.TimestampTolerance.TotalSeconds - 30;
        byte[] body = _envelope.EncryptRequest("login", LoginData(MollyTestKeys.NewKeyHash()), timestamp: stale);

        HttpResponseMessage response = await PostRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithAFutureTimestamp_IsBadRequest()
    {
        long future = MollyTestEnvelope.Now() + (long)MollyRequestProtector.TimestampTolerance.TotalSeconds + 30;
        byte[] body = _envelope.EncryptRequest("login", LoginData(MollyTestKeys.NewKeyHash()), timestamp: future);

        HttpResponseMessage response = await PostRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithAReplayedNonce_IsBadRequest()
    {
        string nonce = MollyTestEnvelope.NewNonce();

        // First use is accepted. A byte-for-byte replay reuses the recorded nonce, which is rejected.
        byte[] first = _envelope.EncryptRequest("ping", $$"""{"id":"{{Guid.NewGuid()}}"}""", nonce);
        Assert.Equal(HttpStatusCode.OK, (await PostRawAsync(first)).StatusCode);

        byte[] replay = _envelope.EncryptRequest("ping", $$"""{"id":"{{Guid.NewGuid()}}"}""", nonce);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRawAsync(replay)).StatusCode);
    }

    [Fact]
    public async Task Request_WithALowOrderEphemeralKey_IsBadRequestNotServerError()
    {
        // An all-zero ephemeral key forces an all-zero ECDH agreement, which the platform throws on.
        // The endpoint must translate that to a rejection, never a 500.
        // 32-byte ephemeral public key + 24-byte nonce + 16-byte tag, minimum-length body.
        byte[] body = new byte[32 + 24 + 16];

        HttpResponseMessage response = await PostRawAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownAction_ReturnsInvalidStatus()
    {
        JsonElement response = await PostAsync("teleport", "{}");

        Assert.Equal("invalid", Status(response));
    }

    [Fact]
    public async Task Associate_ReturnsOk()
    {
        (string id, _) = await RegisterAsync();

        JsonElement response = await PostAsync("associate", $$"""{"id":"{{id}}","nickname":"api-user"}""");

        Assert.Equal("ok", Status(response));
    }

    [Fact]
    public async Task Associate_TooLongNickname_ReturnsInvalid()
    {
        (string id, _) = await RegisterAsync();
        string nickname = new('a', MollyService.MaxNicknameLengthInBytes + 1);

        JsonElement response = await PostAsync("associate", $$"""{"id":"{{id}}","nickname":"{{nickname}}"}""");

        Assert.Equal("invalid", Status(response));
    }

    [Fact]
    public async Task Ping_ReturnsPong()
    {
        (string id, _) = await RegisterAsync();

        JsonElement response = await PostAsync("ping", $$"""{"id":"{{id}}"}""");

        Assert.Equal("ok", Status(response));
        Assert.Equal("pong", Data(response).GetProperty("response").GetString());
    }

    [Fact]
    public async Task Ping_UnknownToken_ReturnsInvalid()
    {
        // A raw guid isn't a token this process issued, so it's rejected before any lookup.
        JsonElement response = await PostAsync("ping", $$"""{"id":"{{Guid.NewGuid()}}"}""");

        Assert.Equal("invalid", Status(response));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"keyHash":null}""")]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("\"string\"")]
    public async Task Login_InvalidData_ReturnsInvalid(string data)
    {
        JsonElement response = await PostAsync("login", data);

        Assert.Equal("invalid", Status(response));
    }

    [Fact]
    public async Task OversizedBody_IsRejectedByTheServer()
    {
        byte[] body = _envelope.EncryptRequest("login", LoginData(new string('A', 20_000)));

        HttpResponseMessage response = await PostRawAsync(body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task BodyAtTheSizeLimit_IsStillProcessed()
    {
        // Comfortably under the 8 KB limit, but far larger than a real request.
        JsonElement response = await PostAsync("login", LoginData(new string('A', 4_000)));

        // The key hash is too long to be valid, but the body was read and decrypted.
        Assert.Equal("invalid", Status(response));
    }

    [Fact]
    public async Task WhenTheSizeLimitCannotBeSet_TheRequestFails()
    {
        byte[] body = _envelope.EncryptRequest("login", LoginData(MollyTestKeys.NewKeyHash()));

        // Rather than reading an unbounded body into memory, the endpoint has to fail closed.
        HttpResponseMessage response = await PostRawAsync(body, extraHeader: RemoveSizeLimitFeatureHeader);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task LockedEntry_ReceivesLockCommandInsteadOfSecrets()
    {
        (string id, _) = await RegisterAsync();

        await _fixture.Service.SetLockRequestedAsync(_fixture.Unprotect(id), lockRequested: true);

        JsonElement response = await PostAsync("ping", $$"""{"id":"{{id}}"}""");

        Assert.Equal("command", Status(response));
        Assert.Equal("lock", Data(response).GetProperty("command").GetString());
    }

    [Fact]
    public async Task WipedEntry_ReceivesWipeCommand()
    {
        (string id, _) = await RegisterAsync();

        await _fixture.Service.RequestWipeAsync(_fixture.Unprotect(id));

        JsonElement response = await PostAsync("ping", $$"""{"id":"{{id}}"}""");
        JsonElement data = Data(response);

        Assert.Equal("wipe", data.GetProperty("command").GetString());
        Assert.False(data.TryGetProperty("serverHmac", out _));
    }

    [Fact]
    public async Task DeletedEntry_ReceivesWipeCommand()
    {
        (string id, _) = await RegisterAsync();

        await _fixture.Service.DeleteEntryAsync(_fixture.Unprotect(id));

        // The token still decrypts, but the entry is gone and its data is unrecoverable, so the
        // device is told to wipe instead of getting a bare "invalid".
        JsonElement response = await PostAsync("ping", $$"""{"id":"{{id}}"}""");

        Assert.Equal("command", Status(response));
        Assert.Equal("wipe", Data(response).GetProperty("command").GetString());
    }

    [Fact]
    public async Task ExcessiveRequests_AreRateLimitedWithRetryAfter()
    {
        string clientIp = NewClientIp();

        HttpResponseMessage? limited = null;

        for (int i = 0; i < 60 && limited is null; i++)
        {
            byte[] body = _envelope.EncryptRequest("ping", $$"""{"id":"{{Guid.NewGuid()}}"}""");
            HttpResponseMessage response = await PostRawAsync(body, clientIp);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        Assert.NotNull(limited);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task Alert_IsAccepted()
    {
        (string id, _) = await RegisterAsync();

        string data = $$$"""{"id":"{{{id}}}","type":"location","data":{"latitude":1.5,"longitude":2.5}}""";

        JsonElement response = await PostAsync("alert", data);

        Assert.Equal("ok", Status(response));

        MollyAlertInfo alert = Assert.Single(
            await _fixture.Service.GetRecentAlertsAsync(),
            a => a.EntryId == _fixture.Unprotect(id));

        Assert.Equal(MollyAlertType.Location, alert.Type);
        Assert.Equal("1.5, 2.5", alert.Summary);
    }

    [Fact]
    public async Task Alert_WithAnUnknownToken_ReturnsInvalid()
    {
        JsonElement response = await PostAsync("alert", $$"""{"id":"{{Guid.NewGuid()}}"}""");

        Assert.Equal("invalid", Status(response));
    }

    [Fact]
    public async Task Alert_ThatIsTooLarge_ReturnsInvalid()
    {
        (string id, _) = await RegisterAsync();

        // Under the 8 KB request limit, but over the alert specific one.
        string data = $$"""{"id":"{{id}}","data":"{{new string('a', MollyService.MaxAlertLength)}}"}""";

        JsonElement response = await PostAsync("alert", data);

        Assert.Equal("invalid", Status(response));
    }

    [Fact]
    public async Task GetRequests_AreNotRouted()
    {
        HttpResponseMessage response = await _fixture.Client.GetAsync(Group);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }
}
