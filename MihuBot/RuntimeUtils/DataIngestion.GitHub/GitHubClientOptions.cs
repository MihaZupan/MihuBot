using System.ComponentModel.DataAnnotations;

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

/// <summary>
/// Configuration options for GitHub client services
/// </summary>
public class GitHubClientOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "GitHub";

    /// <summary>
    /// GitHub personal access token for API authentication
    /// </summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Product name for GitHub API requests (User-Agent header)
    /// </summary>
    [Required]
    public string ProductName { get; set; } = "MihuBot";

    /// <summary>
    /// Optional list of additional tokens to use for API requests to bypass rate limits.
    /// </summary>
    public string[] AdditionalTokens { get; set; } = [];
}
