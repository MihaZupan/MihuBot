using Microsoft.EntityFrameworkCore;
using MihuBot.DB;

namespace Microsoft.Extensions.DependencyInjection;

public static class DbServiceCollectionExtensions
{
    public static void AddDatabases(this IServiceCollection services)
    {
        services.AddPooledDbContextFactory<LogsDbContext>(options =>
        {
            options.UseSqlite($"Data Source={Constants.StateDirectory}/MihuBot-logs.db");

            if (!OperatingSystem.IsLinux())
            {
                options.EnableSensitiveDataLogging();
            }
        });

        services.AddPooledDbContextFactory<MihuBotDbContext>(options =>
        {
            options.UseSqlite($"Data Source={Constants.StateDirectory}/MihuBot.db");

            if (!OperatingSystem.IsLinux())
            {
                options.EnableSensitiveDataLogging();
            }
        });
    }

    public static async Task RunDatabaseMigrations(this IHost host)
    {
        using (var scope = host.Services.CreateScope())
        {
            Console.WriteLine($"Applying {nameof(LogsDbContext)} migrations ...");

            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LogsDbContext>>();
            await using var db = await factory.CreateDbContextAsync();

            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            Console.WriteLine($"Applying {nameof(MihuBotDbContext)} migrations ...");

            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MihuBotDbContext>>();
            await using var db = await factory.CreateDbContextAsync();

            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
    }
}
