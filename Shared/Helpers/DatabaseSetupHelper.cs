using Microsoft.EntityFrameworkCore;

#nullable disable

namespace MihuBot.Helpers;

public static class DatabaseSetupHelper
{
    public static void AddPooledDbContextFactory<TDbContext>(IServiceCollection services, string databasePath)
        where TDbContext : DbContext
    {
        services.AddPooledDbContextFactory<TDbContext>(options =>
        {
            options.UseSqlite($"Data Source={databasePath}");

            if (!OperatingSystem.IsLinux())
            {
                options.EnableSensitiveDataLogging();
            }
        });
    }

    public static async Task MigrateAsync<TDbContext>(IHost host, string databasePath)
        where TDbContext : DbContext
    {
        using IServiceScope scope = host.Services.CreateScope();

        Console.WriteLine($"Applying {typeof(TDbContext).Name} migrations ...");

        IDbContextFactory<TDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TDbContext>>();
        await using TDbContext db = factory.CreateDbContext();

        string tempCopyPath = null;

        if (OperatingSystem.IsWindows() && File.Exists(databasePath))
        {
            Console.WriteLine("DEBUG: Deleting existing database ...");
            await db.Database.EnsureDeletedAsync();
        }

        if (File.Exists(databasePath))
        {
            if (!(await db.Database.GetPendingMigrationsAsync()).Any())
            {
                Console.WriteLine("No pending migrations");
                return;
            }

            tempCopyPath = $"{Path.ChangeExtension(databasePath, null)}-copy.tmp";

            if (File.Exists(tempCopyPath))
            {
                throw new InvalidOperationException($"Backup copy already exists: {tempCopyPath}");
            }

            Console.WriteLine($"Creating a backup copy of {Path.GetFileName(databasePath)}");
            File.Copy(databasePath, tempCopyPath, true);
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
