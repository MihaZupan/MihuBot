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
}
