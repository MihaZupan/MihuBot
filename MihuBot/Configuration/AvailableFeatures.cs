using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils;
using MihuBot.RuntimeUtils.AI;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using MihuBot.RuntimeUtils.Search;

namespace MihuBot.Configuration;

/// <summary>
/// Which optional features are available in this deployment. Used to hide UI for disabled functionality.
/// </summary>
public sealed class AvailableFeatures(IServiceProvider services, IConfiguration configuration)
{
    public bool Minecraft => Has<MinecraftRCON>();
    public bool RuntimeUtils => Has<RuntimeUtilsService>();
    public bool GitHubData => Has<IDbContextFactory<GitHubDbContext>>();
    public bool GitHubIngestion => Has<GitHubDataIngestionService>();
    public bool GitHubSearch => Has<GitHubSearchService>();
    public bool GitHubTriage => Has<IssueTriageHelper>();
    public bool GitHubLogin => configuration.IsConfigured(OptionalFeatures.GitHubOAuth);
    public bool DiscordLogin => configuration.IsConfigured(OptionalFeatures.DiscordOAuth);

    private bool Has<T>() => services.GetService<T>() is not null;
}
