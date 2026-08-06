namespace MihuBot.Configuration;

/// <summary>
/// An integration that is only enabled when all of its configuration keys are present.
/// </summary>
/// <param name="Keys">
/// Keys in <see cref="IConfiguration"/>, or in <see cref="IConfigurationService"/> when <see cref="RuntimeConfiguration"/> is set.
/// </param>
public sealed record OptionalFeature(string Description, params string[] Keys)
{
    public bool RuntimeConfiguration { get; init; }
}

public static class OptionalFeatures
{
    /// <summary>Not optional, but validated the same way on startup.</summary>
    public static readonly OptionalFeature Discord = new("The Discord bot itself", $"Discord:AuthToken{Constants.DevSuffix}");

    public static readonly OptionalFeature DiscordOAuth = new("Signing in with Discord", $"Discord:ClientSecret{Constants.DevSuffix}");

    public static readonly OptionalFeature AppInsights = new("Azure Monitor telemetry", "AppInsights:ConnectionString");
    public static readonly OptionalFeature AzureOpenAI = new("Everything AI (chat, image generation, GitHub search/triage, MCP server)", "AzureOpenAI:Key");
    public static readonly OptionalFeature AzureOpenAIImage = new("Image generation", "AzureOpenAI:ImageKey");
    public static readonly OptionalFeature AzureOpenAISecondaryChat = new("Secondary chat endpoint (falls back to the primary one)", "AzureOpenAI:SecondaryChat:Endpoint", "AzureOpenAI:SecondaryChat:Key");
    public static readonly OptionalFeature AzureOpenAISecondaryEmbedding = new("Secondary embedding endpoint (falls back to the primary one)", "AzureOpenAI:SecondaryEmbedding:Endpoint", "AzureOpenAI:SecondaryEmbedding:Key");
    public static readonly OptionalFeature AzureStorage = new("Archiving Discord attachments to blob storage", "AzureStorage:ConnectionString");
    public static readonly OptionalFeature AzureStorageRuntimeUtils = new("Fuzzing coverage reports and jitdiff extra assemblies", "AzureStorage:ConnectionString-RuntimeUtils");
    public static readonly OptionalFeature GitHub = new("GitHub API access (runtime-utils jobs, data ingestion, self-update)", "GitHub:Token");
    public static readonly OptionalFeature GitHubOAuth = new("Signing in with GitHub", $"GitHub:ClientId{Constants.DevSuffix}", $"GitHub:ClientSecret{Constants.DevSuffix}");
    public static readonly OptionalFeature GitHubDatabase = new("GitHub data database (ingestion, search, triage, duplicate detection)", "GitHub-PostgreSQL:ConnectionString");
    public static readonly OptionalFeature Hetzner = new("Hetzner runner VMs (jobs fall back to Azure VMs)", "Hetzner:ApiKey");
    public static readonly OptionalFeature Jellyfin = new("Jellyfin integration (!pirate)", "Jellyfin:Host", "Jellyfin:ApiKey");
    public static readonly OptionalFeature GoogleMaps = new("Map images for Telegram location updates", "GoogleMaps:ApiKey");
    public static readonly OptionalFeature Minecraft = new("Minecraft RCON (!mc, minecraft-remote page)", "Minecraft:Host", "Minecraft:RconPassword");

    /// <summary>Mixed into the stored key hashes. Never leaves the server. Base64 encoded key material.</summary>
    public const string MollyServerKeyName = "Molly:ServerKey";

    /// <summary>
    /// Shared with (and hardcoded into) the closed-source Molly client, which signs its requests with it.
    /// Base64 encoded key material.
    /// </summary>
    public const string MollyAppSecretName = "Molly:AppSecret";

    public static readonly OptionalFeature Molly = new("Molly remote lockout support (api/Molly, molly page)", MollyServerKeyName, MollyAppSecretName);

    /// <summary>Azure Communication Services connection string used to send Molly alert emails.</summary>
    public const string MollyAlertEmailConnectionStringName = "Molly:AlertEmailConnectionString";

    /// <summary>The verified sender address the alert emails are sent from.</summary>
    public const string MollyAlertEmailFromName = "Molly:AlertEmailFrom";

    /// <summary>
    /// Who the alert emails go to - a comma-separated list. Runtime configuration
    /// (<see cref="IConfigurationService"/>), so recipients can be changed without a redeploy.
    /// Unset means no emails are sent.
    /// </summary>
    public const string MollyAlertEmailToName = "Molly.AlertEmailTo";

    public static readonly OptionalFeature MollyAlertEmail = new(
        $"Molly alert emails, in addition to the Discord notification (recipients come from the runtime '{MollyAlertEmailToName}')",
        MollyAlertEmailConnectionStringName, MollyAlertEmailFromName);

    public static readonly OptionalFeature OpenWeather = new("Weather and location lookups (!weather)", "OpenWeather:ApiKey");
    public static readonly OptionalFeature QBittorrent = new("Torrent downloads (!pirate)", "QBittorrent:Host", "QBittorrent:Username", "QBittorrent:Password");
    public static readonly OptionalFeature Qdrant = new("Vector search and semantic ingestion", "Qdrant:Host");
    public static readonly OptionalFeature Spotify = new("Spotify tracks and playlists (!play)", "Spotify:ClientId", "Spotify:ClientSecret");
    public static readonly OptionalFeature Telegram = new("Telegram relay bot", "TelegramBot:ApiKey");
    public static readonly OptionalFeature Tenor = new("Tenor gifs (!emote)", "Tenor:ApiKey");
    public static readonly OptionalFeature Youtube = new("YouTube data API (search and playlists)", "Youtube:ApiKey");

    public static readonly OptionalFeature[] All =
    [
        AppInsights,
        AzureOpenAI,
        AzureOpenAIImage,
        AzureOpenAISecondaryChat,
        AzureOpenAISecondaryEmbedding,
        AzureStorage,
        AzureStorageRuntimeUtils,
        DiscordOAuth,
        GitHub,
        GitHubDatabase,
        GitHubOAuth,
        GoogleMaps,
        Hetzner,
        Jellyfin,
        Minecraft,
        Molly,
        MollyAlertEmail,
        OpenWeather,
        QBittorrent,
        Qdrant,
        Spotify,
        Telegram,
        Tenor,
        Youtube,
    ];

    public static bool IsConfigured(this IConfiguration configuration, OptionalFeature feature) =>
        feature.Keys.All(key => !string.IsNullOrWhiteSpace(configuration[key]));

    public static bool IsConfigured(this IConfigurationService configuration, OptionalFeature feature) =>
        feature.Keys.All(key => configuration.TryGet(null, key, out string value) && !string.IsNullOrWhiteSpace(value));

    public static OptionalFeature[] GetMissingFeatures(IConfiguration configuration, IConfigurationService configurationService) =>
        [.. All.Where(feature => feature.RuntimeConfiguration
            ? !configurationService.IsConfigured(feature)
            : !configuration.IsConfigured(feature))];
}
