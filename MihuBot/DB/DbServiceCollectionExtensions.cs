using Microsoft.EntityFrameworkCore;
using MihuBot.DB;

namespace Microsoft.Extensions.DependencyInjection;

public static class DbServiceCollectionExtensions
{
    private static string GetDatabasePath<TDBContext>() =>
        typeof(TDBContext) == typeof(LogsDbContext) ? $"{Constants.StateDirectory}/MihuBot-logs.db" :
        typeof(TDBContext) == typeof(MihuBotDbContext) ? $"{Constants.StateDirectory}/MihuBot.db" :
        throw new NotSupportedException();


    public static void AddDatabases(this IServiceCollection services)
    {
        AddPooledDbContextFactory<LogsDbContext>(services);
        AddPooledDbContextFactory<MihuBotDbContext>(services);
    }

    public static async Task RunDatabaseMigrations(this IHost host)
    {
        await MigrateAsync<LogsDbContext>(host);
        await MigrateAsync<MihuBotDbContext>(host);
    }

    private static void AddPooledDbContextFactory<TDbContext>(IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddPooledDbContextFactory<TDbContext>(options =>
        {
            options.UseSqlite($"Data Source={GetDatabasePath<TDbContext>()}");

            if (!OperatingSystem.IsLinux())
            {
                options.EnableSensitiveDataLogging();
            }
        });
    }

    private static async Task MigrateAsync<TDbContext>(IHost host)
        where TDbContext : DbContext
    {
        using var scope = host.Services.CreateScope();

        Console.WriteLine($"Applying {typeof(TDbContext).Name} migrations ...");

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TDbContext>>();
        await using var db = factory.CreateDbContext();

        string path = GetDatabasePath<TDbContext>();
        string tempCopyPath = null;

        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            Console.WriteLine("DEBUG: Deleting existing database ...");
            await db.Database.EnsureDeletedAsync();
        }

        if (File.Exists(path))
        {
            if (!(await db.Database.GetPendingMigrationsAsync()).Any())
            {
                Console.WriteLine("No pending migrations");
                return;
            }

            tempCopyPath = $"{Path.ChangeExtension(path, null)}-copy.tmp";

            if (File.Exists(tempCopyPath))
            {
                throw new InvalidOperationException($"Backup copy already exists: {tempCopyPath}");
            }

            Console.WriteLine($"Creating a backup copy of {Path.GetFileName(path)}");
            File.Copy(path, tempCopyPath, true);
        }

        await db.Database.MigrateAsync();

        Console.WriteLine($"Migrated {typeof(TDbContext).Name}");

        if (tempCopyPath is not null)
        {
            Console.WriteLine($"Deleting backup copy ({tempCopyPath})");
            File.Delete(tempCopyPath);
        }
    }
}
