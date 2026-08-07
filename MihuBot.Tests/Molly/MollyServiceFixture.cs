using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MihuBot.DB;
using MihuBot.Helpers;
using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

/// <summary>
/// Test keys. These only ever exist in tests - the real ones come from configuration.
/// The database key is base64 32-byte key material for <see cref="MollyIdProtector"/> and the at-rest
/// encryption; the transport private key is a raw 32-byte X25519 private key for
/// <see cref="MollyRequestProtector"/>, and the client seals requests to its matching public key.
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

    /// <summary>The public half of <see cref="TransportPrivateKey"/>, which the client seals requests to.</summary>
    public static byte[] TransportPublicKeyBytes => PublicKeyOf(TransportPrivateKeyBytes);

    /// <summary>The public half of <see cref="OtherTransportPrivateKey"/>.</summary>
    public static byte[] OtherTransportPublicKeyBytes => PublicKeyOf(OtherTransportPrivateKeyBytes);

    private static byte[] PublicKeyOf(byte[] privateKey)
    {
        using X25519DiffieHellman key = X25519DiffieHellman.ImportPrivateKey(privateKey);
        return key.ExportPublicKey();
    }
}

/// <summary>
/// A <see cref="MollyService"/> backed by a throw-away SQLite database.
/// </summary>
public sealed class MollyServiceFixture : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"molly-tests-{Guid.NewGuid():N}.db");
    private ServiceProvider _services = null!;

    public MollyService Service { get; private set; } = null!;

    public MollyIdProtector IdProtector { get; } = new(MollyTestKeys.DatabaseKeyBytes);

    public IDbContextFactory<MollyDbContext> DbFactory { get; private set; } = null!;

    /// <summary>Recovers the real entry id from the opaque token handed to the client.</summary>
    public Guid Unprotect(string? protectedId)
    {
        Assert.True(IdProtector.TryUnprotect(protectedId, out Guid id));
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

        Service = CreateService(MollyTestKeys.DatabaseKey, MollyTestKeys.TransportPrivateKey);
    }

    /// <summary>Creates another service over the same database, e.g. to test a different key.</summary>
    public MollyService CreateService(string databaseKey, string transportPrivateKey)
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
        return new MollyService(DbFactory, _services.GetRequiredService<ILogger<MollyService>>(), IdProtector, configuration, discordLogger: null);
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

    public async Task SetLastSeenAsync(Guid id, DateOnly lastSeen)
    {
        await using MollyDbContext db = DbFactory.CreateDbContext();
        (await db.Entries.FirstAsync(e => e.Id == id)).LastSeenDay = lastSeen;
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
        }
        catch { }
    }
}
