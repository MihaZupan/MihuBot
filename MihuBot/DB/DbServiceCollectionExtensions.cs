using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.DB.GitHub;
using MihuBot.DB.GitHubFts;

namespace Microsoft.Extensions.DependencyInjection;

public static class DbServiceCollectionExtensions
{
    private static string GetDatabasePath<TDBContext>() =>
        typeof(TDBContext) == typeof(LogsDbContext) ? $"{Constants.StateDirectory}/MihuBot-logs.db" :
        typeof(TDBContext) == typeof(MihuBotDbContext) ? $"{Constants.StateDirectory}/MihuBot.db" :
        typeof(TDBContext) == typeof(GitHubDbContext) ? $"{Constants.StateDirectory}/GitHubData.db" :
        throw new NotSupportedException();


    public static void AddDatabases(this IServiceCollection services, IConfiguration configuration)
    {
        DatabaseSetupHelper.AddPooledDbContextFactory<LogsDbContext>(services, GetDatabasePath<LogsDbContext>());
        DatabaseSetupHelper.AddPooledDbContextFactory<MihuBotDbContext>(services, GetDatabasePath<MihuBotDbContext>());
        DatabaseSetupHelper.AddPooledDbContextFactory<GitHubDbContext>(services, GetDatabasePath<GitHubDbContext>());

        services.AddPooledDbContextFactory<GitHubFtsDbContext>(options =>
        {
            options.UseSqlServer(configuration["GitHubFts-SqlServer:ConnectionString"]);

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
        await DatabaseSetupHelper.MigrateAsync<GitHubDbContext>(host, GetDatabasePath<GitHubDbContext>());

        await DatabaseSetupHelper.MigrateSqlServerAsync<GitHubFtsDbContext>(host);
    }
}
