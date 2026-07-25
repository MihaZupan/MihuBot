using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MihuBot.Configuration;
using Octokit;

#nullable enable

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

public static class HostApplicationBuilderExtensions
{
    /// <summary>
    /// Adds data ingestion services including GitHub clients and background services
    /// </summary>
    /// <param name="builder">The host application builder</param>
    /// <param name="configureOptions">Optional configuration override for testing scenarios</param>
    public static IHostApplicationBuilder AddGitHubDataIngestion(
        this IHostApplicationBuilder builder, 
        Action<GitHubClientOptions>? configureOptions = null)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        if (!configuration.IsConfigured(OptionalFeatures.GitHub))
        {
            // No GitHub token, everything talking to GitHub stays disabled.
            return builder;
        }

        // Configure and validate GitHub options
        services.Configure<GitHubClientOptions>(configuration.GetSection(GitHubClientOptions.SectionName));
        
        // Apply optional overrides for testing
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }
        
        services.AddOptionsWithValidateOnStart<GitHubClientOptions>();

        // Register GitHub clients
        services.AddSingleton(sp =>
        {
            GitHubClientOptions options = sp.GetRequiredService<IOptions<GitHubClientOptions>>().Value;

            return new GitHubClient(new ProductHeaderValue(options.ProductName))
            {
                Credentials = new Credentials(options.Token)
            };
        });

        services.AddSingleton(sp =>
        {
            GitHubClientOptions options = sp.GetRequiredService<IOptions<GitHubClientOptions>>().Value;
            ILogger<GithubGraphQLClient> logger = sp.GetRequiredService<ILogger<GithubGraphQLClient>>();

            return new GithubGraphQLClient(options.ProductName, [options.Token], logger);
        });

        if (!configuration.IsConfigured(OptionalFeatures.GitHubDatabase))
        {
            // Ingestion has nowhere to store the data.
            return builder;
        }

        // Register data ingestion services
        services.AddSingleton<GitHubDataIngestionService>();
        services.AddHostedService(sp => sp.GetRequiredService<GitHubDataIngestionService>());

        // Semantic ingestion requires a Qdrant instance to write the vectors to and AzureOpenAI for the embeddings.
        if (configuration.IsConfigured(OptionalFeatures.Qdrant) && configuration.IsConfigured(OptionalFeatures.AzureOpenAI))
        {
            services.AddHostedService<GitHubSemanticSearchIngestionService>();
        }

        return builder;
    }
}
