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
}
