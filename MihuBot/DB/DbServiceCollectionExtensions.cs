using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.DB.GitHub;

namespace Microsoft.Extensions.DependencyInjection;

public static class DbServiceCollectionExtensions
{
    private static string GetDatabasePath<TDBContext>() =>
        typeof(TDBContext) == typeof(LogsDbContext) ? $"{Constants.StateDirectory}/MihuBot-logs.db" :
        typeof(TDBContext) == typeof(MihuBotDbContext) ? $"{Constants.StateDirectory}/MihuBot.db" :
        typeof(TDBContext) == typeof(StorageDbContext) ? $"{Constants.StateDirectory}/MihuBot-storage.db" :
        typeof(TDBContext) == typeof(MollyDbContext) ? $"{Constants.StateDirectory}/MihuBot-molly.db" :
        throw new NotSupportedException();


    public static void AddDatabases(this IServiceCollection services, IConfiguration configuration)
    {
        DatabaseSetupHelper.AddPooledDbContextFactory<LogsDbContext>(services, GetDatabasePath<LogsDbContext>());
        DatabaseSetupHelper.AddPooledDbContextFactory<MihuBotDbContext>(services, GetDatabasePath<MihuBotDbContext>());
        DatabaseSetupHelper.AddPooledDbContextFactory<StorageDbContext>(services, GetDatabasePath<StorageDbContext>());
        DatabaseSetupHelper.AddPooledDbContextFactory<MollyDbContext>(services, GetDatabasePath<MollyDbContext>());

        if (configuration.IsConfigured(OptionalFeatures.GitHubDatabase))
        {
            services.AddPooledDbContextFactory<GitHubDbContext>(options =>
            {
                options.UseNpgsql(configuration["GitHub-PostgreSQL:ConnectionString"]);

                if (!OperatingSystem.IsLinux())
                {
                    options.EnableSensitiveDataLogging();
                }
            });
        }
    }

    public static async Task RunDatabaseMigrations(this IHost host)
    {
        try
        {
            await DatabaseSetupHelper.MigrateAsync<LogsDbContext>(host, GetDatabasePath<LogsDbContext>());
            await DatabaseSetupHelper.MigrateAsync<MihuBotDbContext>(host, GetDatabasePath<MihuBotDbContext>());
            await DatabaseSetupHelper.MigrateAsync<StorageDbContext>(host, GetDatabasePath<StorageDbContext>());
            await DatabaseSetupHelper.MigrateAsync<MollyDbContext>(host, GetDatabasePath<MollyDbContext>());

            if (OperatingSystem.IsLinux() &&
                host.Services.GetService<IDbContextFactory<GitHubDbContext>>() is not null)
            {
                await DatabaseSetupHelper.MigrateRemoteServerAsync<GitHubDbContext>(host);
            }
        }
        finally
        {
            DatabaseSetupHelper.NotifyMigrationsCompleted();
        }
    }
}
