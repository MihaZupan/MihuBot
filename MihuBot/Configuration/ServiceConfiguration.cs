namespace MihuBot.Configuration;

public sealed class ServiceConfiguration(IConfigurationService configuration)
{
    private readonly IConfigurationService _configuration = configuration;

    public bool LoggerTrace
    {
        get => Get(nameof(LoggerTrace));
        set => Set(nameof(LoggerTrace), value);
    }

    public bool PauseGitHubPolling
    {
        get => Get(nameof(PauseGitHubPolling));
        set => Set(nameof(PauseGitHubPolling), value);
    }

    public bool PauseSemanticIngestion
    {
        get => Get(nameof(PauseSemanticIngestion));
        set => Set(nameof(PauseSemanticIngestion), value);
    }

    public bool DisableVectorSearch
    {
        get => Get(nameof(DisableVectorSearch));
        set => Set(nameof(DisableVectorSearch), value);
    }

    public bool DisableFullTextSearch
    {
        get => Get(nameof(DisableFullTextSearch));
        set => Set(nameof(DisableFullTextSearch), value);
    }

    public bool PauseAutoTriage
    {
        get => Get(nameof(PauseAutoTriage));
        set => Set(nameof(PauseAutoTriage), value);
    }

    public bool PauseGitHubNCLNotificationPolling
    {
        get => Get(nameof(PauseGitHubNCLNotificationPolling));
        set => Set(nameof(PauseGitHubNCLNotificationPolling), value);
    }

    public bool PauseGitHubNCLMentionPolling
    {
        get => Get(nameof(PauseGitHubNCLMentionPolling));
        set => Set(nameof(PauseGitHubNCLMentionPolling), value);
    }

    public bool PauseAutoDuplicateDetection
    {
        get => Get(nameof(PauseAutoDuplicateDetection));
        set => Set(nameof(PauseAutoDuplicateDetection), value);
    }

    private bool Get(string name) => _configuration.GetOrDefault(null, name, false);

    private void Set(string name, bool value) => _configuration.Set(null, name, value.ToString());
}
