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
        DatabaseSetupHelper.AddPooledDbContextFactory<LogsDbContext>(services, GetDatabasePath<LogsDbContext>());
        DatabaseSetupHelper.AddPooledDbContextFactory<MihuBotDbContext>(services, GetDatabasePath<MihuBotDbContext>());
    }

    public static async Task RunDatabaseMigrations(this IHost host)
    {
        await DatabaseSetupHelper.MigrateAsync<LogsDbContext>(host, GetDatabasePath<LogsDbContext>());
        await DatabaseSetupHelper.MigrateAsync<MihuBotDbContext>(host, GetDatabasePath<MihuBotDbContext>());
    }
}
