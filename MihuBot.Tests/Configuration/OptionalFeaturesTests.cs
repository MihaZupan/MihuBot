using Microsoft.Extensions.Configuration;
using MihuBot.Configuration;
using MihuBot.Helpers.AI;

namespace MihuBot.Tests.Configuration;

public sealed class OptionalFeaturesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void PersonalAzureSettingGatesPersonalServicesButNotWorkOrOtherIntegrations(string? setting, bool enabled)
    {
        var keys = OptionalFeatures.All
            .SelectMany(feature => feature.Keys)
            .Distinct()
            .ToDictionary(key => key, _ => (string?)"test-value");
        keys["Azure:PersonalEnabled"] = setting;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(keys).Build();

        Assert.Equal(enabled, configuration.IsPersonalAzureEnabled());

        foreach (OptionalFeature feature in new[]
        {
            OptionalFeatures.AzureOpenAI, OptionalFeatures.AzureOpenAI2, OptionalFeatures.AzureOpenAI3,
            OptionalFeatures.AzureStorage, OptionalFeatures.AzureStorageRuntimeUtils, OptionalFeatures.MollyAlertEmail,
        })
        {
            Assert.Equal(enabled, configuration.IsConfigured(feature));
        }

        Assert.True(configuration.IsConfigured(OptionalFeatures.AzureOpenAIWork1));
        Assert.True(configuration.IsConfigured(OptionalFeatures.AzureOpenAIWork2));
        Assert.True(configuration.IsConfigured(OptionalFeatures.GitHubDatabase));
        Assert.True(configuration.IsConfigured(OptionalFeatures.Hetzner));
        Assert.True(configuration.IsOpenAIConfigured());
        Assert.True(configuration.IsOpenAIEmbeddingConfigured());
    }

    [Fact]
    public void DisabledPersonalCredentialsCannotEnableAI()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:PersonalEnabled"] = "false",
            ["AzureOpenAI:Key"] = "test-key",
        }).Build();

        Assert.False(configuration.IsOpenAIConfigured());
        Assert.False(configuration.IsOpenAIEmbeddingConfigured());
        Assert.Throws<InvalidOperationException>(() => new OpenAIService(configuration, new TestConfigurationService(), null!));
    }

    [Theory]
    [InlineData("Key2")]
    [InlineData("Key3")]
    [InlineData("WorkKey2")]
    public void ChatOnlyEndpointsDoNotEnableEmbeddingDependentServices(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"AzureOpenAI:{key}"] = "test-key",
        }).Build();

        Assert.True(configuration.IsOpenAIConfigured());
        Assert.False(configuration.IsOpenAIEmbeddingConfigured());
    }
}
