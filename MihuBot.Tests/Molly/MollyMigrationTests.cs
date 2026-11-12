using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MihuBot.DB;
using MihuBot.Molly;

namespace MihuBot.Tests.Molly;

public sealed class MollyMigrationTests
{
    [Fact]
    public async Task EncryptedDeviceStatusMigration_DropsPlaintextStatusButKeepsRegistration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MollyDbContext(new DbContextOptionsBuilder<MollyDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20260912131524_MollyAppVersion");

        Guid id = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mollyEntries
                (Id, HashPrefix, DerivedHash, CreatedAt, LastSeenAt, LockRequested, WipeRequested, AlertsMuted,
                 BatteryLevel, LocationEnabled, AppVersion)
            VALUES ({id}, 0, {new byte[64]}, {now}, {now}, 0, 0, 0, 42, 1, 'legacy-plaintext-version')
            """);

        await migrator.MigrateAsync();

        MollyDbEntry entry = await db.Entries.SingleAsync();
        Assert.Equal(id, entry.Id);
        Assert.Null(entry.EncryptedDeviceStatus);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('mollyEntries')
            WHERE name IN ('BatteryLevel', 'LocationEnabled', 'AppVersion')
            """;
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AttestationMigration_KeepsLegacyRegistrationsUnenrolled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MollyDbContext(new DbContextOptionsBuilder<MollyDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20260912132117_MollyEncryptedDeviceStatus");

        Guid id = Guid.NewGuid();
        byte[] serverHmac = [1, 2, 3];
        DateTime now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mollyEntries
                (Id, HashPrefix, DerivedHash, EncryptedServerHmac, CreatedAt, LastSeenAt, LockRequested, WipeRequested, AlertsMuted)
            VALUES ({id}, 0, {new byte[64]}, {serverHmac}, {now}, {now}, 0, 0, 0)
            """);

        await migrator.MigrateAsync();

        MollyDbEntry entry = await db.Entries.SingleAsync();
        Assert.Equal(id, entry.Id);
        Assert.Equal(serverHmac, entry.EncryptedServerHmac);
        Assert.Null(entry.EncryptedAttestationState);
        Assert.Equal(DateTime.MinValue, entry.LastLoginAt);
        Assert.Equal(DateTime.MinValue, entry.ActiveSessionIssuedAt);
    }

    [Fact]
    public async Task AttestationMigration_RefusesDuplicateRegistrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MollyDbContext(new DbContextOptionsBuilder<MollyDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20260912132117_MollyEncryptedDeviceStatus");

        DateTime now = DateTime.UtcNow;

        foreach (Guid id in (Guid[])[Guid.NewGuid(), Guid.NewGuid()])
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO mollyEntries
                    (Id, HashPrefix, DerivedHash, CreatedAt, LastSeenAt, LockRequested, WipeRequested, AlertsMuted)
                VALUES ({id}, 0, {new byte[64]}, {now}, {now}, 0, 0, 0)
                """);
        }

        // They hold different secrets, so they can't be merged automatically.
        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync());
        Assert.Contains("mollyEntries.DerivedHash", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, await db.Entries.CountAsync());
    }
}
