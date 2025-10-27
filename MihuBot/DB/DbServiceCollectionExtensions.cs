using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.DB.GitHub;

namespace Microsoft.Extensions.DependencyInjection;

public static class DbServiceCollectionExtensions
{
    private static string GetDatabasePath<TDBContext>() =>
        typeof(TDBContext) == typeof(LogsDbContext) ? $"{Constants.StateDirectory}/MihuBot-logs.db" :
        typeof(TDBContext) == typeof(MihuBotDbContext) ? $"{Constants.StateDirectory}/MihuBot.db" :
        throw new NotSupportedException();


    public static void AddDatabases(this IServiceCollection services, IConfiguration configuration)
    {
        DatabaseSetupHelper.AddPooledDbContextFactory<LogsDbContext>(services, GetDatabasePath<LogsDbContext>());
        DatabaseSetupHelper.AddPooledDbContextFactory<MihuBotDbContext>(services, GetDatabasePath<MihuBotDbContext>());

        services.AddPooledDbContextFactory<GitHubDbContext>(options =>
        {
            options.UseNpgsql(configuration["GitHub-PostgreSQL:ConnectionString"]);

            if (!OperatingSystem.IsLinux())
            {
                options.EnableSensitiveDataLogging();
            }
        });
    }

    public static async Task RunDatabaseMigrations(this IHost host)
    {
        await DatabaseSetupHelper.MigrateAsync<LogsDbContext>(host, GetDatabasePath<LogsDbContext>());
        await DatabaseSetupHelper.MigrateAsync<MihuBotDbContext>(host, GetDatabasePath<MihuBotDbContext>());

        //if (OperatingSystem.IsLinux()) // TODO
        {
            await DatabaseSetupHelper.MigrateRemoteServerAsync<GitHubDbContext>(host);
        }
    }
}
