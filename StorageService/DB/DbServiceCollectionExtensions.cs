using StorageService.DB;

namespace Microsoft.Extensions.DependencyInjection;

public static class DbServiceCollectionExtensions
{
    private static string GetDatabasePath<TDBContext>() =>
        typeof(TDBContext) == typeof(StorageDbContext) ? $"{Constants.StateDirectory}/MihuBot-storage.db" :
        throw new NotSupportedException();


    public static void AddDatabases(this IServiceCollection services)
    {
        DatabaseSetupHelper.AddPooledDbContextFactory<StorageDbContext>(services, GetDatabasePath<StorageDbContext>());
    }

    public static async Task RunDatabaseMigrations(this IHost host)
    {
        await DatabaseSetupHelper.MigrateAsync<StorageDbContext>(host, GetDatabasePath<StorageDbContext>());
    }
}
