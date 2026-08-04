using MihuBot.Discord;

namespace MihuBot.Configuration;

/// <summary>
/// Reports which optional integrations are disabled due to missing configuration.
/// </summary>
public sealed class OptionalFeatureReportService(IConfiguration configuration, IConfigurationService configurationService, InitializedDiscordClient discord, Logger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OptionalFeature[] missing = OptionalFeatures.GetMissingFeatures(configuration, configurationService);

        if (missing.Length == 0)
        {
            Console.WriteLine("All optional integrations are configured.");
            return;
        }

        var report = new StringBuilder();
        report.AppendLine("Missing configuration, the following functionality is disabled:");

        foreach (OptionalFeature feature in missing)
        {
            report.AppendLine($"- {feature.Description} ({string.Join(", ", feature.Keys)})");
        }

        string message = report.ToString();

        Console.WriteLine(message);

        try
        {
            await discord.WaitUntilInitializedAsync();

            await logger.DebugAsync(message, truncateToFile: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't post the missing configuration report to Discord: {ex.Message}");
        }
    }
}
