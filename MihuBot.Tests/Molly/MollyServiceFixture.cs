using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.Helpers;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Attestation;

namespace MihuBot.Tests.Molly;

/// <summary>
/// Test keys. These only ever exist in tests - the real ones come from configuration.
/// The database key is base64 32-byte key material for <see cref="MollySealedIdTokens"/> and the at-rest
/// encryption; the transport private key is a raw 32-byte X25519 private key for
/// <see cref="MollyRequestProtector"/>, used by the client only to discover rotating transport keys.
/// </summary>
public static class MollyTestKeys
{
    public const string DatabaseKey = "bW9sbHktdGVzdC1zZXJ2ZXIta2V5LTAxMjM0NTY3ODk=";
    public const string TransportPrivateKey = "bW9sbHktdGVzdC1hcHAtc2VjcmV0LTAxMjM0NTY3ODk=";

    /// <summary>A second set, for checking that a mismatched key doesn't validate.</summary>
    public const string OtherDatabaseKey = "bW9sbHktb3RoZXItc2VydmVyLWtleS0wMTIzNDU2Nzg=";
    public const string OtherTransportPrivateKey = "bW9sbHktb3RoZXItYXBwLXNlY3JldC0wMTIzNDU2Nzg=";

    /// <summary>A random, correctly encoded client key hash.</summary>
    public static string NewKeyHash() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static byte[] DatabaseKeyBytes => Convert.FromBase64String(DatabaseKey);

    public static byte[] OtherDatabaseKeyBytes => Convert.FromBase64String(OtherDatabaseKey);

    public static byte[] TransportPrivateKeyBytes => Convert.FromBase64String(TransportPrivateKey);

    public static byte[] OtherTransportPrivateKeyBytes => Convert.FromBase64String(OtherTransportPrivateKey);

    public static MollySealedIdTokens CreateIdTokens(ReadOnlySpan<byte> databaseKey) =>
        MollySealedIdTokens.CreateFromDatabaseKey(databaseKey, "MihuBot.Molly.MollyIdProtector.v1"u8, urlSafe: false);

    public static MollySealedIdTokens CreateIdTokens() => CreateIdTokens(DatabaseKeyBytes);

    /// <summary>The public half of <see cref="TransportPrivateKey"/>, used to authenticate key discovery.</summary>
    public static byte[] TransportPublicKeyBytes => PublicKeyOf(TransportPrivateKeyBytes);

    /// <summary>The public half of <see cref="OtherTransportPrivateKey"/>.</summary>
    public static byte[] OtherTransportPublicKeyBytes => PublicKeyOf(OtherTransportPrivateKeyBytes);

    private static byte[] PublicKeyOf(byte[] privateKey)
    {
        using X25519DiffieHellman key = X25519DiffieHellman.ImportPrivateKey(privateKey);
        return key.ExportPublicKey();
    }
}

public static class MollyServiceTestExtensions
{
    /// <summary>The party hash of the identity <see cref="AssociateAsync"/> binds every entry to.</summary>
    public static readonly byte[] TestIdentityPartyHash = SHA512.HashData("molly-test-identity"u8);

    /// <summary>
    /// <c>associate</c> with a verified proof for <see cref="TestIdentityPartyHash"/>, for tests that are about the
    /// nickname and session rules rather than the identity.
    /// </summary>
    public static Task<MollyCommandResult> AssociateAsync(this MollyService molly, string? protectedId, string? nickname, CancellationToken cancellationToken = default) =>
        molly.AssociateAsync(protectedId, nickname, TestIdentityPartyHash, MollyTestSessions.ProvenExchange(protectedId, MollyApiActions.Associate), cancellationToken);

    public static Task<MollyCommandResult> IssueRegistrationTokenAsync(this MollyService molly, string? protectedId, CancellationToken cancellationToken = default) =>
        molly.IssueRegistrationTokenAsync(protectedId, MollyTestSessions.ProvenExchange(protectedId, MollyApiActions.RegistrationToken), cancellationToken);

    public static Task<MollyCommandResult> IssueDashboardLoginAsync(this MollyService molly, string? protectedId, CancellationToken cancellationToken = default) =>
        molly.IssueDashboardLoginAsync(new MollyDashboardLoginRequest { Id = protectedId }, MollyTestSessions.ProvenExchange(protectedId, MollyApiActions.DashboardLogin), cancellationToken);
}

/// <summary>
/// The server secret released with every session the tests log in, so they can prove the session like an unlocked
/// device does. Session tokens are unique, so one registry serves every fixture.
/// </summary>
public static class MollyTestSessions
{
    private static readonly ConcurrentDictionary<string, byte[]> s_serverHmacs = new(StringComparer.Ordinal);

    public static void Remember(string sessionId, byte[] serverHmac) => s_serverHmacs[sessionId] = serverHmac;

    public static void Remember(MollyLoginResult login)
    {
        if (login is { ProtectedId: { } sessionId, ServerHmac: { } serverHmac })
        {
            Remember(sessionId, serverHmac);
        }
    }

    /// <summary>
    /// <paramref name="exchange"/> (or a new one) for <paramref name="action"/>, carrying the session proof an unlocked device
    /// sends; without a known session (e.g. an invalid one), it carries none.
    /// </summary>
    public static MollyExchangeContext ProvenExchange(string? sessionId, string action, MollyExchangeContext? exchange = null)
    {
        exchange = (exchange ?? MollyTestDevice.NewExchange()) with { Action = action, SessionProof = null };

        return sessionId is not null && s_serverHmacs.TryGetValue(sessionId, out byte[]? serverHmac)
            ? exchange with { SessionProof = MollySessionProof.Compute(serverHmac, sessionId, exchange) }
            : exchange;
    }
}

/// <summary>
/// A <see cref="MollyService"/> backed by a throw-away SQLite database.
/// </summary>
public sealed class MollyServiceFixture : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"molly-tests-{Guid.NewGuid():N}.db");
    private readonly string _revocationCachePath = Path.Combine(Path.GetTempPath(), $"molly-tests-revocations-{Guid.NewGuid():N}.json");
    private readonly ConcurrentDictionary<string, MollyTestDevice> _devices = new(StringComparer.Ordinal);
    private ServiceProvider _services = null!;

    public MollyService Service { get; private set; } = null!;

    public MollySealedIdTokens IdTokens { get; } = MollyTestKeys.CreateIdTokens();

    public IDbContextFactory<MollyDbContext> DbFactory { get; private set; } = null!;

    /// <summary>Starts out with a fresh, empty revocation list. Never touches the network.</summary>
    public MollyAttestationRevocations Revocations { get; private set; } = null!;

    /// <summary>Recovers the real entry id from the opaque token handed to the client.</summary>
    public Guid Unprotect([NotNull] string? protectedId)
    {
        Assert.True(IdTokens.TryUnprotect(protectedId, out Guid id, out _));
        return id;
    }

    public async Task InitializeAsync()
    {
        // The periodic cleanup task waits on this before touching the database.
        DatabaseSetupHelper.NotifyMigrationsCompleted();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddPooledDbContextFactory<MollyDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));

        _services = services.BuildServiceProvider();

        DbFactory = _services.GetRequiredService<IDbContextFactory<MollyDbContext>>();

        await using (MollyDbContext db = DbFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        Revocations = CreateRevocations(_revocationCachePath);
        Assert.True(Revocations.Apply("""{"entries":{}}"""u8, DateTimeOffset.UtcNow));

        Service = CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey);
    }

    public static MollyAttestationRevocations CreateRevocations(string cachePath, TimeProvider? time = null) =>
        new(cachePath, new HttpClient(new UnreachableHandler()), time ?? TimeProvider.System, NullLogger<MollyAttestationRevocations>.Instance);

    public MollyAttestationService CreateAttestationService(TimeProvider? time = null, MollyAttestationRevocations? revocations = null, IEnumerable<MollyAttestationApp>? apps = null) =>
        new(apps ?? [MollyTestPki.App], MollyTestPki.Instance.TrustAnchors, revocations ?? Revocations, NullLogger<MollyAttestationService>.Instance, time ?? TimeProvider.System);

    /// <summary>
    /// Logs in like the Android client: sends an attested login from the device simulated for
    /// <paramref name="keyHash"/> (or <paramref name="device"/>), bound to a new exchange.
    /// </summary>
    /// <param name="withRegistrationToken">
    /// Whether to include a fresh dashboard registration token, so an unknown key hash registers like it used to
    /// before tokens. Ignored with <paramref name="sessionId"/> or <paramref name="registrationToken"/>.
    /// </param>
    /// <param name="sessionId">A session to move to <paramref name="keyHash"/> if it's unknown, as in a passphrase change.</param>
    /// <param name="registrationToken">A specific registration token to send.</param>
    /// <param name="withSessionProof">Whether to prove <paramref name="sessionId"/> like an unlocked device.</param>
    public async Task<MollyLoginResult> LoginAsync(string? keyHash, MollyService? service = null, MollyTestDevice? device = null, MollyTestProofOptions? options = null, MollyExchangeContext? exchange = null, bool withRegistrationToken = true, string? sessionId = null, string? registrationToken = null, bool withSessionProof = true)
    {
        service ??= Service;
        device ??= _devices.GetOrAdd(keyHash ?? "", _ => new MollyTestDevice());
        exchange ??= MollyTestDevice.NewExchange();

        byte[] keyHashBytes = TryDecodeBase64(keyHash);
        MollyAttestationProof proof = device.CreateProof(keyHashBytes, exchange, options);

        string? token = registrationToken ?? (sessionId is null && withRegistrationToken
            ? service.RegistrationTokens.IssueForDashboard().Token
            : null);

        exchange = sessionId is not null && withSessionProof
            ? MollyTestSessions.ProvenExchange(sessionId, MollyApiActions.Login, exchange)
            : exchange with { Action = MollyApiActions.Login };

        MollyLoginResult result = await service.LoginAsync(new MollyLoginRequest { KeyHash = keyHash, Attestation = proof, RegistrationToken = token, Id = sessionId }, exchange, default);
        MollyTestSessions.Remember(result);
        return result;
    }

    /// <summary>Uses the device simulated for <paramref name="keyHash"/> for <paramref name="otherKeyHash"/> as well, as after a passphrase change.</summary>
    public MollyTestDevice ShareDevice(string keyHash, string otherKeyHash)
    {
        MollyTestDevice device = _devices.GetOrAdd(keyHash, _ => new MollyTestDevice());
        _devices[otherKeyHash] = device;
        return device;
    }

    private static byte[] TryDecodeBase64(string? value)
    {
        try
        {
            return value is null ? [] : Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    /// <summary>Creates another service over the same database, e.g. to test a different key.</summary>
    public MollyService CreateService(string databaseKey, string transportPrivateKey, MollyAttestationService? attestation = null, ServiceConfiguration? serviceConfiguration = null, TimeProvider? timeProvider = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Molly:DatabaseKey"] = databaseKey,
                ["Molly:TransportPrivateKey"] = transportPrivateKey,
            })
            .Build();

        // No Discord connection in tests. MollyService treats that as "don't announce alerts" and
        // skips its maintenance loop, which the tests drive explicitly instead.
        return new MollyService(DbFactory, _services.GetRequiredService<ILogger<MollyService>>(), attestation ?? CreateAttestationService(), configuration, discordLogger: null, serviceConfiguration: serviceConfiguration, timeProvider: timeProvider);
    }

    public async Task<MollyDbEntry> GetEntryAsync(Guid id)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        return await db.Entries.FirstAsync(e => e.Id == id);
    }

    public async Task<bool> EntryExistsAsync(Guid id)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        return await db.Entries.AnyAsync(e => e.Id == id);
    }

    public async Task SetEncryptedServerHmacAsync(Guid id, byte[]? serverHmac)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        (await db.Entries.FirstAsync(e => e.Id == id)).EncryptedServerHmac = serverHmac;
        await db.SaveChangesAsync();
    }

    public async Task SetEncryptedNicknameAsync(Guid id, byte[]? nickname)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        (await db.Entries.FirstAsync(e => e.Id == id)).EncryptedNickname = nickname;
        await db.SaveChangesAsync();
    }

    public async Task SetLastSeenAsync(Guid id, DateTime lastSeen)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        (await db.Entries.FirstAsync(e => e.Id == id)).LastSeenAt = lastSeen;
        await db.SaveChangesAsync();
    }

    public async Task SetLastLoginAsync(Guid id, DateTime lastLogin)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        (await db.Entries.FirstAsync(e => e.Id == id)).LastLoginAt = lastLogin;
        await db.SaveChangesAsync();
    }

    public async Task SetEncryptedDeviceStatusAsync(Guid id, byte[]? status)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        (await db.Entries.FirstAsync(e => e.Id == id)).EncryptedDeviceStatus = status;
        await db.SaveChangesAsync();
    }

    public async Task<int> CountEntriesAsync()
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        return await db.Entries.CountAsync();
    }

    public async Task<int> CountAlertsAsync(Guid entryId)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        return await db.Alerts.CountAsync(a => a.EntryId == entryId);
    }

    public async Task<byte[][]> GetRawAlertPayloadsAsync(Guid entryId)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        return await db.Alerts.Where(a => a.EntryId == entryId).Select(a => a.EncryptedPayload).ToArrayAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();

        try
        {
            File.Delete(_databasePath);
            File.Delete(_revocationCachePath);
        }
        catch { }
    }
}

/// <summary>Fails every request, so tests can never reach the real revocation list.</summary>
public sealed class UnreachableHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("Tests don't use the network.");
}
